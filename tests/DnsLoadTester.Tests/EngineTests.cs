using DnsLoadTester.Core.Engine;
using DnsLoadTester.Core.Output;
using DnsLoadTester.Core.Report;
using DnsLoadTester.Core.Stats;

namespace DnsLoadTester.Tests;

/// <summary>
/// Scenari completi del motore contro il server finto: ritmo, interruzioni, riconnessioni, TLS, DoH, arresto manuale,
/// file di sessione e report. Sono test lenti (circa due minuti in totale): misurano il comportamento nel tempo.
/// </summary>
[Collection("engine")]
public class EngineTests(EngineFixture fx)
{
    private TestConfig Base(DnsProtocol p, int port) => new()
    {
        Server = "127.0.0.1", Port = port, Protocol = p,
        Queries = ["example.test A", "example.test AAAA", "www.example.test MX", "{rnd}.example.test A"],
        OutputFolder = fx.OutDir, TimeoutMs = 1000, HealthWindowSeconds = 5,
    };

    private static async Task<(LoadEngine, LiveSnapshot)> Run(TestConfig cfg)
    {
        var eng = new LoadEngine(cfg, "test");
        await eng.StartAsync();
        await eng.Completion;
        return (eng, eng.Stats!.Snapshot!);
    }

    [Fact]
    public async Task Probe_ReturnsEveryAnswerIncludingNxDomain()
    {
        var (resolved, results, _) = await LoadEngine.ProbeAsync(Base(DnsProtocol.Udp, fx.Server.UdpPort), 10, CancellationToken.None);
        Assert.Contains("127.0.0.1", resolved);
        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.True(r.IsResponse && r.Authoritative));
        Assert.Contains(results, r => r.Rcode == 3);
    }

    [Fact]
    public async Task Probe_DecodesExtendedDnsError()
    {
        var cfg = Base(DnsProtocol.Udp, fx.Server.UdpPort);
        cfg.Queries = ["refused.example.test A"];
        var (_, res, _) = await LoadEngine.ProbeAsync(cfg, 1, CancellationToken.None);
        Assert.Equal(5, res[0].Rcode);
        Assert.Equal("EDE 18 Prohibited: recursion not allowed", res[0].Detail);
    }

    [Fact]
    public async Task Udp_KeepsRateThroughOutage_AndWritesEveryFile()
    {
        fx.Server.Reset(dropPct: 1, outageFrom: 12, outageTo: 20);
        var cfg = Base(DnsProtocol.Udp, fx.Server.UdpPort);
        cfg.TargetQps = 800; cfg.Concurrency = 420; cfg.DurationSeconds = 70; cfg.TimeoutMs = 500;
        Assert.Empty(cfg.Warnings());
        var (eng, snap) = await Run(cfg);

        Assert.InRange(snap.Total.Sent / 70.0, 760, 840);                  // il ritmo regge anche durante l'interruzione
        Assert.True(snap.Total.Completed > 50_000);
        Assert.True(snap.Total.Timeouts > 6000);
        Assert.True(snap.Incidents >= 1);
        Assert.True(snap.HealthSeconds["NON DISPONIBILE"] >= 6);
        Assert.InRange(snap.Total.LatP50Ms, 0.5, 10);
        Assert.True(snap.Outcomes.GetValueOrDefault("NXDOMAIN") > 0);

        var d = eng.SessionFolder;
        foreach (var f in new[] { "session.json", "summary.json", "intervals_1s.csv", "intervals_1m.csv", "events.csv", "app.log", "queries_0001.csv", "report_v1.0.html" })
            Assert.True(File.Exists(Path.Combine(d, f)), f);
        long qlines = Directory.GetFiles(d, "queries_*.csv").Sum(f => File.ReadLines(f).Count() - 1);
        Assert.Equal(snap.Total.Completed, qlines);                          // nessuna query persa nel log
        Assert.InRange(File.ReadLines(Path.Combine(d, "intervals_1s.csv")).Count() - 1, 69, 71);
        Assert.Equal(3, File.ReadLines(Path.Combine(d, "intervals_1m.csv")).Count());

        var csv = new CsvFormat(CsvDialect.ExcelItalian);
        var sec = new List<long>(); var min = new List<long>();
        IntervalWriter.ReadEach(Path.Combine(d, "intervals_1s.csv"), csv, r => sec.Add(r.Index), 1);
        IntervalWriter.ReadEach(Path.Combine(d, "intervals_1m.csv"), csv, r => min.Add(r.Index), 60);
        Assert.True(sec.Select((v, i) => v == i).All(x => x));             // indici contigui dal secondo 0
        Assert.Equal(new long[] { 0, 1 }, min);

        var html = File.ReadAllText(Path.Combine(d, "report_v1.0.html"));
        Assert.Contains("<svg", html);
        Assert.Contains("NON DISPONIBILE", html);
        Assert.Equal("report_v1.1.html", Path.GetFileName(ReportGenerator.Generate(d)));   // rigenerazione numerata
    }

    [Fact]
    public async Task Tcp_ReconnectsTransparentlyWhenServerClosesConnections()
    {
        fx.Server.Reset(closeEvery: 50);
        var cfg = Base(DnsProtocol.Tcp, fx.Server.TcpPort);
        cfg.TargetQps = 500; cfg.Concurrency = 20; cfg.DurationSeconds = 8;
        var (_, snap) = await Run(cfg);
        Assert.True(snap.Total.SuccessPct > 99.9, $"successo {snap.Total.SuccessPct}");
        Assert.True(snap.ConnectionsOpened > 50);
    }

    [Fact]
    public async Task DoT_Works_AndRejectsUntrustedCertificate()
    {
        fx.Server.Reset();
        var cfg = Base(DnsProtocol.DoT, fx.Server.DotPort);
        cfg.TlsSkipValidation = true; cfg.TlsServerName = "localhost";
        cfg.TargetQps = 400; cfg.Concurrency = 10; cfg.DurationSeconds = 6;
        var (_, snap) = await Run(cfg);
        Assert.True(snap.Total.SuccessPct > 99.9 && snap.Total.Completed > 2000);

        cfg.TlsSkipValidation = false;
        var (_, res, _) = await LoadEngine.ProbeAsync(cfg, 1, CancellationToken.None);
        Assert.Equal(QueryOutcome.TlsError, res[0].Outcome);
    }

    [Theory]
    [InlineData(DohMethod.Post)]
    [InlineData(DohMethod.Get)]
    public async Task DoH_Http2_BothMethods(DohMethod method)
    {
        fx.Server.Reset();
        var cfg = Base(DnsProtocol.DoH, 0);
        cfg.DohUrl = fx.Server.DohUrl; cfg.DohMethod = method; cfg.DohHttpVersion = DohHttpVersion.Http2;
        cfg.TlsSkipValidation = true; cfg.TargetQps = 400; cfg.Concurrency = 10; cfg.DurationSeconds = 6;
        var (_, snap) = await Run(cfg);
        Assert.True(snap.Total.SuccessPct > 99.9 && snap.Total.Completed > 2000);
    }

    [Fact]
    public async Task ManualStop_Unlimited_FailuresOnlyLog_AndPartialReport()
    {
        fx.Server.Reset(servfailPct: 5);
        var cfg = Base(DnsProtocol.Udp, fx.Server.UdpPort);
        cfg.TargetQps = 0; cfg.Concurrency = 8; cfg.DurationSeconds = 0; cfg.QueryLog = QueryLogMode.FailuresOnly;
        var eng = new LoadEngine(cfg, "test");
        await eng.StartAsync();
        await Task.Delay(4000);
        await eng.Stats!.FlushSummaryAsync(TimeSpan.FromSeconds(3));
        var partial = ReportGenerator.Generate(eng.SessionFolder, partial: true);
        Assert.Contains("report parziale", File.ReadAllText(partial));
        eng.Stop();
        await eng.Completion;

        var snap = eng.Stats!.Snapshot!;
        Assert.Equal(EngineState.Finished, eng.State);
        Assert.InRange(snap.QueryLogLines, 1, snap.Total.Completed / 10);
        Assert.Contains("\"Interrotto\"", File.ReadAllText(Path.Combine(eng.SessionFolder, "summary.json")));
    }
}
