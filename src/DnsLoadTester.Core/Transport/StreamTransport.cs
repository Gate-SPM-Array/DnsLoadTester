using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace DnsLoadTester.Core.Transport;

/// <summary>
/// DNS su TCP (RFC 7766) e su TLS (DoT, RFC 7858): messaggi preceduti da 2 byte di lunghezza.
/// La connessione viene riusata tra le query (se richiesto) e riaperta automaticamente se il server la chiude.
/// </summary>
public sealed class StreamTransport(TransportContext ctx, bool tls) : DnsTransport
{
    private Socket? _socket;
    private Stream? _stream;
    private readonly byte[] _sendBuf = new byte[65537];
    private readonly byte[] _lenBuf = new byte[2];

    private async ValueTask<Stream> ConnectAsync(CancellationToken ct)
    {
        var s = new Socket(ctx.EndPoint!.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await s.ConnectAsync(ctx.EndPoint, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            s.Dispose();
            throw new DnsTransportException(TransportErrorKind.Network, L.F("Connessione TCP fallita: {0}", ex.SocketErrorCode), ex);
        }
        catch { s.Dispose(); throw; }

        Stream stream = new NetworkStream(s, ownsSocket: true);
        if (tls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = ctx.TlsHost,
                    RemoteCertificateValidationCallback = ctx.CertCallback,
                }, ct).ConfigureAwait(false);
            }
            catch (AuthenticationException ex)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw new DnsTransportException(TransportErrorKind.Tls, L.F("Handshake TLS fallito: {0}", ex.Message), ex);
            }
            catch (IOException ex)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw new DnsTransportException(TransportErrorKind.Tls, L.F("Handshake TLS interrotto: {0}", ex.Message), ex);
            }
            catch
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            stream = ssl;
        }
        _socket = s;
        _stream = stream;
        Interlocked.Increment(ref ConnectionsOpened);
        return stream;
    }

    public override async ValueTask<int> ExchangeAsync(ReadOnlyMemory<byte> request, ushort id, Memory<byte> response, CancellationToken ct)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_sendBuf, (ushort)request.Length);
        request.Span.CopyTo(_sendBuf.AsSpan(2));
        int sendLen = request.Length + 2;

        for (int attempt = 0; ; attempt++)
        {
            bool reused = _stream != null;
            var stream = _stream ?? await ConnectAsync(ct).ConfigureAwait(false);
            bool gotData = false;
            try
            {
                await stream.WriteAsync(_sendBuf.AsMemory(0, sendLen), ct).ConfigureAwait(false);
                int r = await stream.ReadAtLeastAsync(_lenBuf, 2, throwOnEndOfStream: false, ct).ConfigureAwait(false);
                if (r < 2) throw new EndOfStreamException(L.T("Connessione chiusa dal server"));
                gotData = true;
                int len = BinaryPrimitives.ReadUInt16BigEndian(_lenBuf);
                if (len > response.Length) throw new DnsTransportException(TransportErrorKind.Protocol, L.F("Risposta troppo grande ({0} byte)", len));
                await stream.ReadExactlyAsync(response[..len], ct).ConfigureAwait(false);
                if (len >= 2 && BinaryPrimitives.ReadUInt16BigEndian(response.Span) != id)
                {
                    Interlocked.Increment(ref StrayResponses);
                    await ResetAsync().ConfigureAwait(false);
                    throw new DnsTransportException(TransportErrorKind.Protocol, L.T("ID risposta non corrispondente"));
                }
                if (!ctx.Config.ReuseConnections) await ResetAsync().ConfigureAwait(false);
                return len;
            }
            catch (OperationCanceledException)
            {
                await ResetAsync().ConfigureAwait(false);   // stato del flusso indeterminato dopo un timeout
                throw;
            }
            catch (DnsTransportException)
            {
                await ResetAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or ObjectDisposedException)
            {
                await ResetAsync().ConfigureAwait(false);
                // Una connessione riusata può essere stata chiusa dal server per inattività: un solo nuovo tentativo.
                if (reused && !gotData && attempt == 0) continue;
                throw new DnsTransportException(TransportErrorKind.Closed, ex is EndOfStreamException ? L.T("Connessione chiusa dal server") : ex.Message, ex);
            }
        }
    }

    private async ValueTask ResetAsync()
    {
        var st = _stream;
        _stream = null;
        _socket = null;
        if (st != null) { try { await st.DisposeAsync().ConfigureAwait(false); } catch { } }
    }

    public override ValueTask DisposeAsync() => ResetAsync();
}
