using DnsLoadTester.Core.Dns;
using DnsLoadTester.Core.Engine;
using DnsLoadTester.Core.Stats;

namespace DnsLoadTester.Tests;

/// <summary>Formato DNS, elenco query, istogramma: parti pure, senza rete.</summary>
public class WireTests
{
    [Fact]
    public void BuildQuery_ProducesExpectedHeaderAndOpt()
    {
        var buf = new byte[512];
        int len = DnsWire.BuildQuery(buf, 0x1234, "www.Esempio.it", 1, new QueryOptions(true, true, 1232, true, false));
        Assert.Equal(0x12, buf[0]); Assert.Equal(0x34, buf[1]);
        Assert.Equal(0x01, buf[2]);                                   // RD
        Assert.Equal(1, buf[11]);                                     // ARCOUNT (OPT)
        Assert.Equal(12 + 16 + 4 + 11, len);                          // header + nome + tipo/classe + OPT
        Assert.Equal(0x80, buf[len - 4]);                             // bit DO
    }

    [Fact]
    public void ParseResponse_MatchesQuestionCaseInsensitively_AndReadsExtendedRcode()
    {
        var q = new byte[512];
        int ql = DnsWire.BuildQuery(q, 7, "example.TEST", 1, new QueryOptions(false, false, 1232, false, false));
        var r = q[..ql].ToArray();
        r[2] = 0x84; r[3] = 0x00;
        // domanda in minuscolo: deve corrispondere comunque (RFC 4343)
        for (int i = 12; i < ql; i++) if (r[i] >= 'A' && r[i] <= 'Z') r[i] += 32;
        Assert.True(DnsWire.TryParseResponse(r, q.AsSpan(0, ql), out var info));
        Assert.True(info.IsResponse && info.Authoritative && info.QuestionMatches);
        Assert.Equal(0, info.Rcode);
        Assert.Equal(-1, info.EdeCode);
    }

    [Theory]
    [InlineData("dominio.it SOA", "dominio.it", 6)]
    [InlineData("MX dominio.it", "dominio.it", 15)]
    [InlineData("dominio.it,TXT", "dominio.it", 16)]
    [InlineData("dominio.it", "dominio.it", 1)]
    [InlineData("città.it A", "xn--citt-3na.it", 1)]
    [InlineData("dominio.it TYPE65", "dominio.it", 65)]
    public void QuerySet_ParsesEveryFormat(string line, string name, ushort type)
    {
        var set = QuerySet.Parse([line, "# commento", ""], "A", QueryOrder.RoundRobin, out var warnings);
        var q = Assert.Single(set.Items);
        Assert.Equal(name, q.Name);
        Assert.Equal(type, q.Type);
        Assert.Empty(warnings);
    }

    [Fact]
    public void QuerySet_RandomLabel_IsTenCharsAndDifferentEachTime()
    {
        var set = QuerySet.Parse(["{rnd}.dominio.it A"], "A", QueryOrder.RoundRobin, out _);
        var a = QuerySet.Materialize(set.Items[0]);
        var b = QuerySet.Materialize(set.Items[0]);
        Assert.Matches("^[a-z0-9]{10}\\.dominio\\.it$", a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void QuerySet_InvalidLabelIsSkippedWithWarning()
    {
        var set = QuerySet.Parse([new string('a', 64) + ".it A", "ok.it A"], "A", QueryOrder.RoundRobin, out var warnings);
        Assert.Single(set.Items);
        Assert.Single(warnings);
    }

    [Fact]
    public void Histogram_PercentilesWithinThreePercent()
    {
        var h = new LatencyHistogram();
        for (int i = 1; i <= 10_000; i++) h.Record(i * 100);         // 0,1 … 1000 ms
        Assert.InRange(h.PercentileMs(50), 500 * 0.97, 500 * 1.03);
        Assert.InRange(h.PercentileMs(99), 990 * 0.97, 990 * 1.03);
        Assert.Equal(1000, h.MaxMs, 3);
    }

    [Fact]
    public void Config_WarnsWhenWorkersCannotSustainRateDuringOutage()
    {
        var c = new TestConfig { TargetQps = 200, Concurrency = 100, TimeoutMs = 2000, OutputFolder = "x", Server = "1.2.3.4" };
        Assert.Equal(400, c.ConcurrencyForSustainedQps);
        Assert.Contains(c.Warnings(), w => w.Contains("400"));
        c.Concurrency = 400;
        Assert.DoesNotContain(c.Warnings(), w => w.Contains("400"));
    }
}
