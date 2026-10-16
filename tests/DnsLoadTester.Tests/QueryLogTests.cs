using DnsLoadTester.Core.Analysis;
using DnsLoadTester.Core.Engine;
using DnsLoadTester.Core.Output;

namespace DnsLoadTester.Tests;

/// <summary>Analisi a posteriori del log per query: lettura, filtri, aggregati, statistiche, esportazione.</summary>
[Collection("engine")]
public class QueryLogTests(EngineFixture fx)
{
    private static string Dir()
    {
        var d = Path.Combine(Path.GetTempPath(), "dnslt_qlog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>File sintetico nel formato del programma: ogni valore è noto, quindi i risultati sono verificabili esattamente.</summary>
    private static string WriteSample(string dir, CsvDialect dialect, string name = "queries_0001.csv")
    {
        var csv = new CsvFormat(dialect);
        var t0 = new DateTime(2026, 10, 16, 10, 0, 0, 250);
        var lines = new List<string> { csv.Join(QueryLogWriter.Header) };
        // 0-9 s: 10 query al secondo, latenze 1..10 ms; al secondo 5 tutte in timeout; nomi casuali → NXDOMAIN
        for (int s = 0; s < 10; s++)
            for (int k = 0; k < 10; k++)
            {
                var ts = t0.AddSeconds(s).AddMilliseconds(k * 50);
                string name2, outcome, rcode, bytes, ans, aa;
                double lat = k + 1;
                if (s == 5) { name2 = "www.esempio.it"; outcome = "TIMEOUT"; rcode = ""; lat = 1000; bytes = ans = aa = ""; }
                else if (k == 9) { name2 = $"abcde{s:D2}xyz.esempio.it"; outcome = "NXDOMAIN"; rcode = "3"; bytes = "90"; ans = "0"; aa = "1"; }
                else if (k == 8) { name2 = "esempio.it"; outcome = "SERVFAIL"; rcode = "2"; bytes = "40"; ans = "0"; aa = "0"; }
                else { name2 = "www.esempio.it"; outcome = "NOERROR"; rcode = "0"; bytes = "60"; ans = "1"; aa = "1"; }
                lines.Add(csv.Join(
                [
                    ts.ToString(CsvFormat.TimestampFormat), ((s * 1000) + k * 50).ToString(), (s * 10 + k + 1).ToString(), "0",
                    csv.Escape(name2), name2.StartsWith("esempio") ? "SOA" : "A", outcome, rcode, csv.Num(lat), bytes, ans, aa,
                    aa.Length > 0 ? "0" : "", outcome == "SERVFAIL" ? csv.Escape("EDE 22 No Reachable Authority; detail") : "",
                ]));
            }
        lines.Add(t0.AddSeconds(11).ToString(CsvFormat.TimestampFormat) + csv.Sep + "11000");   // riga troncata (crash)
        var path = Path.Combine(dir, name);
        File.WriteAllLines(path, lines, SessionFiles.Utf8Bom);
        return path;
    }

    [Theory]
    [InlineData(CsvDialect.ExcelItalian)]
    [InlineData(CsvDialect.Standard)]
    public void Load_ReadsBothDialects_AndSkipsTruncatedLines(CsvDialect dialect)
    {
        var dir = Dir();
        try
        {
            var log = QueryLog.Load([WriteSample(dir, dialect)]);
            Assert.Equal(100, log.Count);
            Assert.Equal(1, log.SkippedLines);
            Assert.Equal(new DateTime(2026, 10, 16, 10, 0, 0), log.StartLocal);
            Assert.Equal(0.25, log.T[0], 3);
            Assert.Equal(4.5f, log.LatMs.Where((_, i) => log.Class[i] == QueryClass.NoError).Take(8).Average(), 3);
            Assert.Equal(10, log.Class.Count(c => c == QueryClass.Timeout));
            Assert.Contains("EDE 22 No Reachable Authority; detail", log.Details);
            Assert.True(log.IsAuthoritative(0));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Filter_Aggregate_Outcomes_PerName()
    {
        var dir = Dir();
        try
        {
            var log = QueryLog.Load([WriteSample(dir, CsvDialect.ExcelItalian)]);
            var all = QueryLogAnalyzer.Filter(log, new QueryFilter());
            Assert.Equal(100, all.Length);

            Assert.Equal(19, QueryLogAnalyzer.Filter(log, new QueryFilter { Mode = OutcomeFilter.FailuresOnly }).Length);   // 9 SERVFAIL + 10 timeout
            Assert.Equal(28, QueryLogAnalyzer.Filter(log, new QueryFilter { Mode = OutcomeFilter.FailuresOnly, NxDomainIsSuccess = false }).Length);
            Assert.Equal(9, QueryLogAnalyzer.Filter(log, new QueryFilter { Mode = OutcomeFilter.Specific, Outcome = "SERVFAIL" }).Length);
            Assert.Equal(9, QueryLogAnalyzer.Filter(log, new QueryFilter { NameContains = "XYZ" }).Length);                  // nomi casuali, senza distinzione maiuscole
            Assert.Equal(9, QueryLogAnalyzer.Filter(log, new QueryFilter { Type = 6 }).Length);                              // SOA
            Assert.Empty(QueryLogAnalyzer.Filter(log, new QueryFilter { Mode = OutcomeFilter.Specific, Outcome = "REFUSED" }));

            var a = QueryLogAnalyzer.Aggregate(log, all, 0, 10, 1, nxIsSuccess: true);
            Assert.Equal(10, a.Completed[0]);
            Assert.Equal(9, a.Success[0]);                        // 8 NOERROR + 1 NXDOMAIN
            Assert.Equal(10, a.Timeouts[5]);
            Assert.True(double.IsNaN(a.P50[5]));                  // nessuna risposta in quel secondo
            Assert.Equal(5, a.P50[0]);                            // latenze 1..10: rango 5 → 5 ms
            Assert.Equal(10, a.P99[0]);

            var coarse = QueryLogAnalyzer.Aggregate(log, all, 0, 10, 5, nxIsSuccess: true);
            Assert.Equal(50, coarse.Completed[0]);
            Assert.Equal(5, coarse.P50[0]);

            var outcomes = QueryLogAnalyzer.Outcomes(log, all, 0, all.Length);
            Assert.Equal("NOERROR", outcomes[0].Outcome);
            Assert.Equal(72, outcomes[0].Count);
            Assert.Equal(100.0, outcomes.Sum(o => o.Percent), 6);

            var names = QueryLogAnalyzer.PerName(log, all, 0, all.Length, nxIsSuccess: true);
            var rnd = Assert.Single(names, n => n.Name.StartsWith("{rnd}"));
            Assert.Equal("{rnd}.esempio.it A", rnd.Name);         // i nomi casuali sono raggruppati
            Assert.Equal(9, rnd.Count);
            var www = Assert.Single(names, n => n.Name == "www.esempio.it A");
            Assert.Equal(82, www.Count);
            Assert.Equal(10, www.Timeouts);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Export_RoundTripsTheSelection()
    {
        var dir = Dir();
        try
        {
            var log = QueryLog.Load([WriteSample(dir, CsvDialect.ExcelItalian)]);
            var fails = QueryLogAnalyzer.Filter(log, new QueryFilter { Mode = OutcomeFilter.FailuresOnly });
            var outPath = Path.Combine(dir, "export_v1.0.csv");
            QueryLogAnalyzer.Export(log, fails, 0, fails.Length, outPath, CsvDialect.Standard);
            var again = QueryLog.Load([outPath]);
            Assert.Equal(fails.Length, again.Count);
            Assert.Equal(0, again.SkippedLines);
            Assert.Equal(log.T[fails[0]], again.T[0] + (again.StartLocal - log.StartLocal).TotalSeconds, 3);
            Assert.Equal(9, again.Class.Count(c => c == QueryClass.ServFail));
            Assert.Contains("EDE 22 No Reachable Authority; detail", again.Details);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_MixedFilesAreSortedByTime_AndBadHeaderIsRejected()
    {
        var dir = Dir();
        try
        {
            var a = WriteSample(dir, CsvDialect.ExcelItalian, "queries_0002.csv");
            var lines = File.ReadAllLines(a).ToList();
            // secondo file con gli stessi orari spostati indietro di un'ora
            var csv = new CsvFormat(CsvDialect.ExcelItalian);
            var shifted = lines.Take(1).Concat(lines.Skip(1).Take(5).Select(l =>
            {
                var f = csv.Split(l);
                f[0] = DateTime.ParseExact(f[0], CsvFormat.TimestampFormat, null).AddHours(-1).ToString(CsvFormat.TimestampFormat);
                return csv.Join(f);
            }));
            File.WriteAllLines(Path.Combine(dir, "queries_0001.csv"), shifted, SessionFiles.Utf8Bom);
            var log = QueryLog.Load(QueryLog.SessionFiles(dir));
            Assert.Equal(105, log.Count);
            for (int i = 1; i < log.Count; i++) Assert.True(log.T[i] >= log.T[i - 1]);
            Assert.Equal(new DateTime(2026, 10, 16, 9, 0, 0), log.StartLocal);

            File.WriteAllText(Path.Combine(dir, "altro.csv"), "a;b;c\n1;2;3\n");
            Assert.Throws<InvalidDataException>(() => QueryLog.Load([Path.Combine(dir, "altro.csv")]));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(60, 1)]
    [InlineData(1200, 1)]
    [InlineData(3600, 5)]
    [InlineData(86400, 120)]
    [InlineData(7 * 86400, 600)]
    public void ChooseBucket_IsRoundAndBounded(double span, double expected) =>
        Assert.Equal(expected, QueryLogAnalyzer.ChooseBucket(span));

    [Fact]
    public async Task RealSession_LogMatchesEngineTotals()
    {
        fx.Server.Reset(servfailPct: 3);
        var cfg = new TestConfig
        {
            Server = "127.0.0.1", Port = fx.Server.UdpPort, Protocol = DnsProtocol.Udp,
            Queries = ["example.test A", "{rnd}.example.test A"], OutputFolder = fx.OutDir,
            TargetQps = 200, Concurrency = 20, DurationSeconds = 5, TimeoutMs = 1000,
        };
        var eng = new LoadEngine(cfg, "test");
        await eng.StartAsync();
        await eng.Completion;
        var snap = eng.Stats!.Snapshot!;

        var log = QueryLog.Load(QueryLog.SessionFiles(eng.SessionFolder));
        Assert.Equal(snap.Total.Completed, log.Count);
        Assert.Equal(snap.Total.ServFail, log.Class.Count(c => c == QueryClass.ServFail));
        Assert.Equal(snap.Total.NxDomain, log.Class.Count(c => c == QueryClass.NxDomain));
        var all = QueryLogAnalyzer.Filter(log, new QueryFilter());
        var agg = QueryLogAnalyzer.Aggregate(log, all, 0, log.SpanSeconds + 1, 1, true);
        Assert.Equal(log.Count, (long)agg.Completed.Sum());
        var names = QueryLogAnalyzer.PerName(log, all, 0, all.Length, true);
        Assert.Contains(names, n => n.Name == "{rnd}.example.test A");
    }
}
