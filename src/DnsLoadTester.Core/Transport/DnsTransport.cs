using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using DnsLoadTester.Core.Engine;

namespace DnsLoadTester.Core.Transport;

/// <summary>Categoria dell'errore di trasporto (finisce nei log e nelle statistiche).</summary>
public enum TransportErrorKind { Network, Tls, Http, Protocol, Closed }

public sealed class DnsTransportException(TransportErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public TransportErrorKind Kind { get; } = kind;
}

/// <summary>Un canale di trasporto usato da un singolo worker (una query alla volta).</summary>
public abstract class DnsTransport : IAsyncDisposable
{
    /// <summary>Numero di connessioni aperte (handshake TCP/TLS/QUIC) da questo trasporto.</summary>
    public long ConnectionsOpened;
    /// <summary>Risposte scartate perché non corrispondenti alla query in corso (es. risposte tardive UDP).</summary>
    public long StrayResponses;

    /// <summary>DoH e DoQ richiedono ID = 0 (RFC 8484 §4.1, RFC 9250 §4.2.1).</summary>
    public virtual bool RequiresZeroId => false;

    /// <summary>Invia la query e scrive la risposta in <paramref name="response"/>. Restituisce la lunghezza.</summary>
    public abstract ValueTask<int> ExchangeAsync(ReadOnlyMemory<byte> request, ushort id, Memory<byte> response, CancellationToken ct);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Risorse condivise tra i worker: endpoint risolto, HttpClient per DoH, opzioni TLS.</summary>
public sealed class TransportContext : IDisposable
{
    public TestConfig Config { get; }
    public IPEndPoint? EndPoint { get; private set; }
    public HttpClient? Http { get; private set; }
    public string TlsHost { get; private set; } = "";
    public string ResolvedDescription { get; private set; } = "";

    private TransportContext(TestConfig cfg) => Config = cfg;

    public static async Task<TransportContext> CreateAsync(TestConfig cfg, CancellationToken ct)
    {
        var ctx = new TransportContext(cfg);
        if (cfg.Protocol == DnsProtocol.DoH)
        {
            var uri = new Uri(cfg.DohUrl);
            ctx.TlsHost = uri.IdnHost;
            if (cfg.DohHttpVersion == DohHttpVersion.Http3 && !QuicConnection.IsSupported)
                throw new PlatformNotSupportedException(L.T("HTTP/3 (QUIC) non è supportato su questo sistema. Su Windows serve Windows 11 o Windows Server 2022 o successivi."));
            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = Math.Max(1, cfg.Concurrency),
                EnableMultipleHttp2Connections = true,
                EnableMultipleHttp3Connections = true,
                ConnectTimeout = TimeSpan.FromMilliseconds(Math.Max(cfg.TimeoutMs, 1000)),
                PooledConnectionLifetime = cfg.ReuseConnections ? Timeout.InfiniteTimeSpan : TimeSpan.Zero,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                UseProxy = false,
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = cfg.TlsSkipValidation ? (_, _, _, _) => true : null,
                },
            };
            ctx.Http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            ctx.ResolvedDescription = cfg.DohUrl;
            return ctx;
        }

        if (cfg.Protocol == DnsProtocol.DoQ && !QuicConnection.IsSupported)
            throw new PlatformNotSupportedException(L.T("DNS over QUIC non è supportato su questo sistema. Su Windows serve Windows 11 o Windows Server 2022 o successivi."));

        IPAddress? ip;
        var host = cfg.Server.Trim().Trim('[', ']');
        if (!IPAddress.TryParse(host, out ip))
        {
            var addrs = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            ip = cfg.IpFamily switch
            {
                4 => addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork),
                6 => addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6),
                _ => addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault(),
            } ?? throw new InvalidOperationException(L.F("Impossibile risolvere '{0}' nella famiglia IP richiesta.", host));
        }
        ctx.EndPoint = new IPEndPoint(ip, cfg.Port);
        ctx.TlsHost = string.IsNullOrWhiteSpace(cfg.TlsServerName) ? host : cfg.TlsServerName.Trim();
        ctx.ResolvedDescription = ctx.EndPoint.ToString();
        return ctx;
    }

    public RemoteCertificateValidationCallback? CertCallback =>
        Config.TlsSkipValidation ? (_, _, _, _) => true : null;

    public DnsTransport CreateTransport() => Config.Protocol switch
    {
        DnsProtocol.Udp => new UdpTransport(this),
        DnsProtocol.Tcp => new StreamTransport(this, tls: false),
        DnsProtocol.DoT => new StreamTransport(this, tls: true),
        DnsProtocol.DoH => new DohTransport(this),
        DnsProtocol.DoQ => QuicConnection.IsSupported ? new DoqTransport(this) : throw new PlatformNotSupportedException(L.T("QUIC non supportato su questo sistema.")),
        _ => throw new NotSupportedException(),
    };

    public void Dispose() => Http?.Dispose();
}
