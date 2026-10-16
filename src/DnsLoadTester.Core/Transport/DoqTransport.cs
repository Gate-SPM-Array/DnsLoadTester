using System.Buffers.Binary;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;

namespace DnsLoadTester.Core.Transport;

/// <summary>
/// DNS over QUIC (RFC 9250): una connessione QUIC per worker, uno stream bidirezionale per query,
/// messaggio con prefisso di lunghezza a 2 byte, ID = 0.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class DoqTransport(TransportContext ctx) : DnsTransport
{
    private const long DoqNoError = 0x0, DoqRequestCancelled = 0x3;
    private QuicConnection? _conn;
    private readonly byte[] _sendBuf = new byte[65537];
    private readonly byte[] _lenBuf = new byte[2];

    public override bool RequiresZeroId => true;

    private async ValueTask<QuicConnection> ConnectAsync(CancellationToken ct)
    {
        try
        {
            var conn = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = ctx.EndPoint!,
                DefaultStreamErrorCode = DoqRequestCancelled,
                DefaultCloseErrorCode = DoqNoError,
                MaxInboundBidirectionalStreams = 0,
                MaxInboundUnidirectionalStreams = 0,
                IdleTimeout = TimeSpan.FromSeconds(30),
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = [new SslApplicationProtocol("doq")],
                    TargetHost = ctx.TlsHost,
                    RemoteCertificateValidationCallback = ctx.CertCallback,
                },
            }, ct).ConfigureAwait(false);
            Interlocked.Increment(ref ConnectionsOpened);
            _conn = conn;
            return conn;
        }
        catch (AuthenticationException ex)
        {
            throw new DnsTransportException(TransportErrorKind.Tls, L.F("Handshake QUIC/TLS fallito: {0}", ex.Message), ex);
        }
        catch (QuicException ex)
        {
            throw new DnsTransportException(TransportErrorKind.Network, L.F("Connessione QUIC fallita: {0}", ex.QuicError), ex);
        }
    }

    public override async ValueTask<int> ExchangeAsync(ReadOnlyMemory<byte> request, ushort id, Memory<byte> response, CancellationToken ct)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_sendBuf, (ushort)request.Length);
        request.Span.CopyTo(_sendBuf.AsSpan(2));
        int sendLen = request.Length + 2;

        for (int attempt = 0; ; attempt++)
        {
            bool reused = _conn != null;
            var conn = _conn ?? await ConnectAsync(ct).ConfigureAwait(false);
            bool gotData = false;
            try
            {
                await using var stream = await conn.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
                await stream.WriteAsync(_sendBuf.AsMemory(0, sendLen), completeWrites: true, ct).ConfigureAwait(false);
                int r = await stream.ReadAtLeastAsync(_lenBuf, 2, throwOnEndOfStream: false, ct).ConfigureAwait(false);
                if (r < 2) throw new DnsTransportException(TransportErrorKind.Closed, L.T("Stream QUIC chiuso senza risposta"));
                gotData = true;
                int len = BinaryPrimitives.ReadUInt16BigEndian(_lenBuf);
                if (len > response.Length) throw new DnsTransportException(TransportErrorKind.Protocol, L.F("Risposta troppo grande ({0} byte)", len));
                await stream.ReadExactlyAsync(response[..len], ct).ConfigureAwait(false);
                if (!ctx.Config.ReuseConnections) await ResetAsync().ConfigureAwait(false);
                return len;
            }
            catch (OperationCanceledException)
            {
                throw; // la connessione QUIC resta valida: si annulla solo lo stream
            }
            catch (QuicException ex)
            {
                await ResetAsync().ConfigureAwait(false);
                if (reused && !gotData && attempt == 0 && ex.QuicError is QuicError.ConnectionIdle or QuicError.ConnectionAborted or QuicError.ConnectionTimeout)
                    continue;
                throw new DnsTransportException(TransportErrorKind.Network, "QUIC: " + ex.QuicError + (ex.ApplicationErrorCode is { } c ? $" ({c})" : ""), ex);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                await ResetAsync().ConfigureAwait(false);
                if (reused && !gotData && attempt == 0) continue;
                throw new DnsTransportException(TransportErrorKind.Closed, ex.Message, ex);
            }
        }
    }

    private async ValueTask ResetAsync()
    {
        var c = _conn;
        _conn = null;
        if (c != null) { try { await c.DisposeAsync().ConfigureAwait(false); } catch { } }
    }

    public override ValueTask DisposeAsync() => ResetAsync();
}
