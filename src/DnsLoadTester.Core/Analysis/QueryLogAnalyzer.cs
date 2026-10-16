using System.Text;
using System.Text.RegularExpressions;
using DnsLoadTester.Core.Dns;
using DnsLoadTester.Core.Output;

namespace DnsLoadTester.Core.Analysis;

public enum OutcomeFilter { All, SuccessOnly, FailuresOnly, Specific }

/// <summary>Criteri di filtro dell'analisi a posteriori.</summary>
public sealed record QueryFilter
{
    public string NameContains { get; init; } = "";
    public ushort? Type { get; init; }
    public OutcomeFilter Mode { get; init; } = OutcomeFilter.All;
    /// <summary>Esito esatto (es. "SERVFAIL") quando <see cref="Mode"/> è <see cref="OutcomeFilter.Specific"/>.</summary>
    public string? Outcome { get; init; }
    public bool NxDomainIsSuccess { get; init; } = true;
}

/// <summary>Aggregati di un intervallo di tempo, a passo fisso (indice assoluto = floor(t / Bucket)).</summary>
public sealed class Aggregates
{
    public double Bucket { get; init; }
    public long FirstIndex { get; init; }
    public int Length { get; init; }
    public double[] Completed = [], Responses = [], Success = [], Timeouts = [], P50 = [], P95 = [], P99 = [];
}

public sealed record OutcomeRow(string Outcome, long Count, double Percent);
public sealed record NameRow(string Name, long Count, double SuccessPct, long Timeouts, long Errors, double AvgMs, double P95Ms, double AaPct);

/// <summary>Filtri, aggregazioni e statistiche sul log per query (nessuna dipendenza grafica: verificabile dai test).</summary>
public static class QueryLogAnalyzer
{
    /// <summary>Indici delle righe che soddisfano il filtro, in ordine di tempo.</summary>
    public static int[] Filter(QueryLog log, QueryFilter f)
    {
        bool[]? nameOk = null;
        if (!string.IsNullOrWhiteSpace(f.NameContains))
        {
            var needle = f.NameContains.Trim();
            nameOk = log.Names.Select(n => n.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        int outcomeId = f.Mode == OutcomeFilter.Specific && f.Outcome != null ? log.Outcomes.IndexOf(f.Outcome) : -1;
        if (f.Mode == OutcomeFilter.Specific && outcomeId < 0) return [];

        var res = new List<int>(log.Count);
        for (int i = 0; i < log.Count; i++)
        {
            if (nameOk != null && !nameOk[log.NameId[i]]) continue;
            if (f.Type is { } t && log.QType[i] != t) continue;
            switch (f.Mode)
            {
                case OutcomeFilter.SuccessOnly when !log.IsSuccess(i, f.NxDomainIsSuccess): continue;
                case OutcomeFilter.FailuresOnly when log.IsSuccess(i, f.NxDomainIsSuccess): continue;
                case OutcomeFilter.Specific when log.OutcomeId[i] != outcomeId: continue;
            }
            res.Add(i);
        }
        return [.. res];
    }

    /// <summary>Primo elemento di <paramref name="rows"/> con T ≥ t (le righe sono in ordine di tempo).</summary>
    public static int LowerBound(QueryLog log, int[] rows, double t)
    {
        int lo = 0, hi = rows.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (log.T[rows[mid]] < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Passo di aggregazione "tondo" che produce al massimo <paramref name="maxBuckets"/> intervalli.</summary>
    public static double ChooseBucket(double spanSeconds, int maxBuckets = 1200)
    {
        double[] steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 21600, 43200, 86400];
        foreach (var s in steps) if (spanSeconds / s <= maxBuckets) return s;
        return steps[^1];
    }

    /// <summary>
    /// Aggrega le righe nell'intervallo [from, to): conteggi per secondo e percentili di latenza esatti per intervallo
    /// (calcolati sulle sole risposte; timeout ed errori di trasporto non hanno una latenza di risposta).
    /// </summary>
    public static Aggregates Aggregate(QueryLog log, int[] rows, double from, double to, double bucket, bool nxIsSuccess)
    {
        long first = (long)Math.Floor(Math.Max(0, from) / bucket);
        long last = (long)Math.Floor(Math.Max(0, to) / bucket);
        int len = (int)Math.Max(1, last - first + 1);
        var a = new Aggregates
        {
            Bucket = bucket, FirstIndex = first, Length = len,
            Completed = new double[len], Responses = new double[len], Success = new double[len], Timeouts = new double[len],
            P50 = new double[len], P95 = new double[len], P99 = new double[len],
        };
        Array.Fill(a.P50, double.NaN); Array.Fill(a.P95, double.NaN); Array.Fill(a.P99, double.NaN);

        int k = LowerBound(log, rows, first * bucket);
        var lats = new List<float>(1024);
        int cur = -1;
        void Close()
        {
            if (cur < 0 || lats.Count == 0) return;
            lats.Sort();
            a.P50[cur] = Pct(lats, 50); a.P95[cur] = Pct(lats, 95); a.P99[cur] = Pct(lats, 99);
            lats.Clear();
        }
        for (; k < rows.Length; k++)
        {
            int i = rows[k];
            long b = (long)Math.Floor(log.T[i] / bucket) - first;
            if (b >= len) break;
            if (b < 0) continue;
            if (b != cur) { Close(); cur = (int)b; }
            a.Completed[b]++;
            if (log.HasResponse(i)) { a.Responses[b]++; lats.Add(log.LatMs[i]); }
            if (log.Class[i] == QueryClass.Timeout) a.Timeouts[b]++;
            if (log.IsSuccess(i, nxIsSuccess)) a.Success[b]++;
        }
        Close();
        return a;
    }

    /// <summary>Percentile con il metodo "nearest rank" (lo stesso dei percentili del test dal vivo).</summary>
    private static double Pct(List<float> sorted, double p)
    {
        int rank = (int)Math.Ceiling(sorted.Count * p / 100.0);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    public static List<OutcomeRow> Outcomes(QueryLog log, int[] rows, int from, int to)
    {
        var counts = new long[log.Outcomes.Count];
        for (int k = from; k < to; k++) counts[log.OutcomeId[rows[k]]]++;
        long tot = Math.Max(1, to - from);
        return counts.Select((c, i) => new OutcomeRow(log.Outcomes[i], c, 100.0 * c / tot))
                     .Where(r => r.Count > 0).OrderByDescending(r => r.Count).ToList();
    }

    private static readonly Regex RandomLabel = new("^[a-z0-9]{10}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Chiave di raggruppamento per nome: i nomi generati da {rnd} (prima etichetta di 10 caratteri [a-z0-9])
    /// sono raggruppati come "{rnd}.resto.del.nome", altrimenti ognuno sarebbe una riga a sé.
    /// </summary>
    public static string GroupKey(string name, ushort type)
    {
        int dot = name.IndexOf('.');
        string key = dot == 10 && RandomLabel.IsMatch(name.AsSpan(0, 10).ToString()) ? "{rnd}" + name[dot..] : name;
        return key + " " + DnsTypes.ToName(type);
    }

    public static List<NameRow> PerName(QueryLog log, int[] rows, int from, int to, bool nxIsSuccess, int top = 500)
    {
        var groups = new Dictionary<string, (long n, long ok, long to, long err, double sum, long resp, long aa, List<float> lats)>(StringComparer.Ordinal);
        var keyCache = new Dictionary<(int, ushort), string>();
        for (int k = from; k < to; k++)
        {
            int i = rows[k];
            var ck = (log.NameId[i], log.QType[i]);
            if (!keyCache.TryGetValue(ck, out var key)) keyCache[ck] = key = GroupKey(log.Names[log.NameId[i]], log.QType[i]);
            groups.TryGetValue(key, out var g);
            g.lats ??= [];
            g.n++;
            if (log.IsSuccess(i, nxIsSuccess)) g.ok++;
            if (log.Class[i] == QueryClass.Timeout) g.to++;
            else if (log.Class[i] == QueryClass.Error) g.err++;
            if (log.HasResponse(i))
            {
                g.resp++; g.sum += log.LatMs[i]; g.lats.Add(log.LatMs[i]);
                if (log.IsAuthoritative(i)) g.aa++;
            }
            groups[key] = g;
        }
        return groups.OrderByDescending(kv => kv.Value.n).Take(top).Select(kv =>
        {
            var g = kv.Value;
            g.lats.Sort();
            return new NameRow(kv.Key, g.n, 100.0 * g.ok / g.n, g.to, g.err,
                g.resp == 0 ? double.NaN : g.sum / g.resp, g.lats.Count == 0 ? double.NaN : Pct(g.lats, 95),
                g.resp == 0 ? double.NaN : 100.0 * g.aa / g.resp);
        }).ToList();
    }

    /// <summary>Esporta le righe indicate nello stesso formato dei file queries_NNNN.csv.</summary>
    public static void Export(QueryLog log, int[] rows, int from, int to, string path, CsvDialect dialect)
    {
        var csv = new CsvFormat(dialect);
        using var w = new StreamWriter(path, false, SessionFiles.Utf8Bom);
        w.WriteLine(csv.Join(QueryLogWriter.Header));
        var sb = new StringBuilder(256);
        char s = csv.Sep;
        for (int k = from; k < to; k++)
        {
            int i = rows[k];
            bool resp = log.HasResponse(i);
            sb.Clear()
              .Append(log.TimeOf(i).ToString(CsvFormat.TimestampFormat)).Append(s)
              .Append(((long)(log.T[i] * 1000)).ToString()).Append(s)
              .Append(k - from + 1).Append(s)
              .Append(log.Worker[i]).Append(s)
              .Append(csv.Escape(log.Names[log.NameId[i]])).Append(s)
              .Append(DnsTypes.ToName(log.QType[i])).Append(s)
              .Append(log.Outcomes[log.OutcomeId[i]]).Append(s)
              .Append(log.Rcode[i] >= 0 ? log.Rcode[i].ToString() : "").Append(s)
              .Append(csv.Num(log.LatMs[i])).Append(s)
              .Append(resp ? log.RespBytes[i].ToString() : "").Append(s)
              .Append(resp ? log.Answers[i].ToString() : "").Append(s)
              .Append(resp ? (log.IsAuthoritative(i) ? "1" : "0") : "").Append(s)
              .Append(resp ? (log.IsTruncated(i) ? "1" : "0") : "").Append(s)
              .Append(log.DetailId[i] >= 0 ? csv.Escape(log.Details[log.DetailId[i]]) : "");
            w.WriteLine(sb);
        }
    }
}
