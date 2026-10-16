using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DnsLoadTester.Tests;

/// <summary>
/// Server DNS finto e controllabile (UDP, TCP, DoT, DoH) per verificare il motore senza rete esterna.
/// Comportamento: nomi di 10 caratteri come prima etichetta → NXDOMAIN; "refused.*" → REFUSED con EDE 18;
/// A → una risposta; perdite, SERVFAIL, interruzioni e chiusure di connessione configurabili con <see cref="Reset"/>.
/// </summary>
public sealed class FakeDnsServer : IAsyncDisposable
{
    private double _drop, _servfail;
    private int _outFrom = -1, _outTo = -1, _closeEvery;
    private long _t0 = Stopwatch.GetTimestamp();
    private readonly CancellationTokenSource _cts = new();
    private readonly UdpClient _udp;
    private readonly TcpListener _tcp, _dot;
    private readonly WebApplication _web;

    public int UdpPort { get; }
    public int TcpPort { get; }
    public int DotPort { get; }
    public string DohUrl { get; }

    public FakeDnsServer()
    {
        var cert = MakeCert();
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        UdpPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _ = Task.Run(() => ServeUdp(_udp, _cts.Token));
        _tcp = new TcpListener(IPAddress.Loopback, 0); _tcp.Start();
        TcpPort = ((IPEndPoint)_tcp.LocalEndpoint).Port;
        _ = Task.Run(() => ServeTcp(_tcp, null, _cts.Token));
        _dot = new TcpListener(IPAddress.Loopback, 0); _dot.Start();
        DotPort = ((IPEndPoint)_dot.LocalEndpoint).Port;
        _ = Task.Run(() => ServeTcp(_dot, cert, _cts.Token));

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, lo =>
        {
            lo.Protocols = HttpProtocols.Http1AndHttp2;
            lo.UseHttps(cert);
        }));
        _web = builder.Build();
        _web.MapPost("/dns-query", async (HttpContext ctx) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            var resp = await Handle(ms.ToArray());
            if (resp == null) { ctx.Response.StatusCode = 503; return; }
            ctx.Response.ContentType = "application/dns-message";
            await ctx.Response.Body.WriteAsync(resp);
        });
        _web.MapGet("/dns-query", async (HttpContext ctx) =>
        {
            var b64 = ctx.Request.Query["dns"].ToString().Replace('-', '+').Replace('_', '/');
            b64 += new string('=', (4 - b64.Length % 4) % 4);
            var resp = await Handle(Convert.FromBase64String(b64));
            if (resp == null) { ctx.Response.StatusCode = 503; return; }
            ctx.Response.ContentType = "application/dns-message";
            await ctx.Response.Body.WriteAsync(resp);
        });
        _web.StartAsync().GetAwaiter().GetResult();
        DohUrl = _web.Urls.First().Replace("127.0.0.1", "localhost") + "/dns-query";
    }

    public void Reset(double dropPct = 0, int outageFrom = -1, int outageTo = -1, int closeEvery = 0, double servfailPct = 0)
    {
        _drop = dropPct; _outFrom = outageFrom; _outTo = outageTo; _closeEvery = closeEvery; _servfail = servfailPct;
        _t0 = Stopwatch.GetTimestamp();
    }

    private async Task<byte[]?> Handle(byte[] q)
    {
        double t = Stopwatch.GetElapsedTime(_t0).TotalSeconds;
        if (t >= _outFrom && t < _outTo) return null;
        if (Random.Shared.NextDouble() * 100 < _drop) return null;
        int delay = Random.Shared.Next(0, 4);
        if (delay > 0) await Task.Delay(delay);
        return Build(q, Random.Shared.NextDouble() * 100 < _servfail);
    }

    private static byte[] Build(byte[] q, bool servfail)
    {
        int p = 12; var labels = new List<string>();
        while (q[p] != 0) { labels.Add(System.Text.Encoding.ASCII.GetString(q, p + 1, q[p])); p += q[p] + 1; }
        p++;
        ushort qtype = BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(p));
        int qEnd = p + 4;
        bool nx = labels[0].Length == 10 && labels.Count == 3;
        var r = new List<byte>(q.AsSpan(0, qEnd).ToArray());
        bool refusedName = labels[0] == "refused";
        int rcode = refusedName ? 5 : servfail ? 2 : nx ? 3 : 0;
        r[2] = (byte)(0x84 | (q[2] & 0x01)); r[3] = (byte)rcode;        // QR, AA, RD copiato
        bool ans = rcode == 0 && qtype == 1;
        r[6] = 0; r[7] = (byte)(ans ? 1 : 0); r[8] = 0; r[9] = 0; r[10] = 0; r[11] = (byte)(refusedName ? 1 : 0);
        if (ans) r.AddRange(new byte[] { 0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0x0E, 0x10, 0, 4, 192, 0, 2, 1 });
        if (refusedName)
        {
            var txt = System.Text.Encoding.ASCII.GetBytes("recursion not allowed");
            int optLen = 4 + 2 + txt.Length;
            r.AddRange(new byte[] { 0, 0, 41, 0x04, 0xD0, 0, 0, 0, 0, (byte)(optLen >> 8), (byte)optLen, 0, 15, (byte)((2 + txt.Length) >> 8), (byte)(2 + txt.Length), 0, 18 });
            r.AddRange(txt);
        }
        return [.. r];
    }

    private async Task ServeUdp(UdpClient u, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult rec;
            try { rec = await u.ReceiveAsync(ct); } catch { return; }
            _ = Task.Run(async () =>
            {
                var resp = await Handle(rec.Buffer);
                if (resp != null) { try { await u.SendAsync(resp, rec.RemoteEndPoint); } catch { } }
            });
        }
    }

    private async Task ServeTcp(TcpListener l, X509Certificate2? cert, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct); } catch { return; }
            _ = Task.Run(async () =>
            {
                using var client = c;
                Stream s = c.GetStream();
                try
                {
                    if (cert != null) { var ssl = new SslStream(s); await ssl.AuthenticateAsServerAsync(cert); s = ssl; }
                    var len = new byte[2]; int served = 0;
                    while (true)
                    {
                        if (await s.ReadAtLeastAsync(len, 2, false) < 2) return;
                        var q = new byte[BinaryPrimitives.ReadUInt16BigEndian(len)];
                        await s.ReadExactlyAsync(q);
                        var resp = await Handle(q);
                        if (resp == null) return;
                        var o = new byte[resp.Length + 2];
                        BinaryPrimitives.WriteUInt16BigEndian(o, (ushort)resp.Length);
                        resp.CopyTo(o, 2);
                        await s.WriteAsync(o);
                        if (_closeEvery > 0 && ++served >= _closeEvery) return;
                    }
                }
                catch { /* connessione chiusa dal client */ }
            });
        }
    }

    private static X509Certificate2 MakeCert()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var c = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx), null);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _udp.Dispose(); _tcp.Stop(); _dot.Stop();
        await _web.StopAsync();
        await _web.DisposeAsync();
    }
}

/// <summary>Server finto e cartella temporanea condivisi dai test del motore.</summary>
public sealed class EngineFixture : IAsyncLifetime
{
    public FakeDnsServer Server { get; private set; } = null!;
    public string OutDir { get; } = Path.Combine(Path.GetTempPath(), "dnslt_tests_" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(OutDir);
        Server = new FakeDnsServer();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Server.DisposeAsync();
        try { Directory.Delete(OutDir, true); } catch { }
    }
}

[CollectionDefinition("engine")]
public sealed class EngineCollection : ICollectionFixture<EngineFixture> { }
