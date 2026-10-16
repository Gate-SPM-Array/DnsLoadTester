using System.Diagnostics;
using System.Net.Sockets;
using DnsLoadTester.Core.Dns;
using DnsLoadTester.Core.Stats;
using DnsLoadTester.Core.Transport;

namespace DnsLoadTester.Core.Engine;

/// <summary>Esegue una query alla volta su un trasporto dedicato, misurandone la latenza. Uno per worker.</summary>
public sealed class QueryRunner : IAsyncDisposable
{
    private readonly TestConfig _cfg;
    private readonly DnsTransport _transport;
    private readonly int _workerId;
    private readonly byte[] _req = new byte[600];
    private readonly byte[] _resp = new byte[65535];
    private readonly QueryOptions _opt;
    private CancellationTokenSource _cts = new();
    private static long _seq;

    public DnsTransport Transport => _transport;

    public QueryRunner(TestConfig cfg, TransportContext ctx, int workerId)
    {
        _cfg = cfg;
        _workerId = workerId;
        _transport = ctx.CreateTransport();
        _opt = new QueryOptions(cfg.RecursionDesired, cfg.UseEdns, (ushort)cfg.EdnsBufferSize, cfg.DnssecOk, false);
    }

    public async Task<QueryResult> RunAsync(QueryTemplate t, Action? onSent = null)
    {
        string qname = QuerySet.Materialize(t);
        ushort id = _transport.RequiresZeroId ? (ushort)0 : (ushort)Random.Shared.Next(0, 65536);
        int len = DnsWire.BuildQuery(_req, id, qname, t.Type, _opt);
        long seq = Interlocked.Increment(ref _seq);

        if (!_cts.TryReset()) { _cts.Dispose(); _cts = new CancellationTokenSource(); }
        _cts.CancelAfter(_cfg.TimeoutMs);
        var ct = _cts.Token;

        QueryOutcome outcome = QueryOutcome.Response;
        int rcode = -1, rlen = 0, answers = 0;
        bool aa = false, tc = false;
        string? detail = null;

        onSent?.Invoke();
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            rlen = await _transport.ExchangeAsync(_req.AsMemory(0, len), id, _resp, ct).ConfigureAwait(false);
            if (!DnsWire.TryParseResponse(_resp.AsSpan(0, rlen), _req.AsSpan(0, len), out var info))
            { outcome = QueryOutcome.ProtocolError; detail = L.F("Risposta malformata ({0} byte)", rlen); }
            else if (!info.IsResponse)
            { outcome = QueryOutcome.ProtocolError; detail = L.T("Bit QR assente nella risposta"); }
            else if (info.Id != id)
            { outcome = QueryOutcome.ProtocolError; detail = L.F("ID risposta {0} ≠ {1}", info.Id, id); }
            else if (!info.QuestionMatches && !(info.Truncated && info.QdCount == 0) && info.Rcode != DnsRcodes.FormErr)
            { outcome = QueryOutcome.ProtocolError; detail = L.T("Sezione question non corrispondente"); rcode = info.Rcode; }
            else
            {
                rcode = info.Rcode;
                if (info.EdeCode >= 0)
                    detail = $"EDE {info.EdeCode} {DnsWire.EdeName(info.EdeCode)}" + (string.IsNullOrEmpty(info.EdeText) ? "" : ": " + info.EdeText);
                answers = info.AnCount;
                aa = info.Authoritative;
                tc = info.Truncated;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = QueryOutcome.Timeout;
        }
        catch (DnsTransportException ex)
        {
            outcome = ex.Kind switch
            {
                TransportErrorKind.Network => QueryOutcome.NetworkError,
                TransportErrorKind.Tls => QueryOutcome.TlsError,
                TransportErrorKind.Http => QueryOutcome.HttpError,
                _ => QueryOutcome.ProtocolError,
            };
            detail = ex.Message;
        }
        catch (SocketException ex)
        {
            outcome = QueryOutcome.NetworkError;
            detail = ex.SocketErrorCode.ToString();
        }
        catch (Exception ex)
        {
            outcome = QueryOutcome.ProtocolError;
            detail = ex.GetType().Name + ": " + ex.Message;
        }
        long t1 = Stopwatch.GetTimestamp();
        long us = (t1 - t0) * 1_000_000 / Stopwatch.Frequency;

        return new QueryResult(seq, DateTime.UtcNow, t1, _workerId, t.Index, qname, t.Type, outcome,
            rcode, (int)Math.Min(us, int.MaxValue), rlen, answers, aa, tc, detail);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
