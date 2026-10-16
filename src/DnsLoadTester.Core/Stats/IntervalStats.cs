using DnsLoadTester.Core.Dns;

namespace DnsLoadTester.Core.Stats;

public enum HealthState : byte { NoData, Ok, Degraded, Down }

/// <summary>Accumulatore mutabile per un intervallo (secondo o minuto).</summary>
public sealed class IntervalAccumulator
{
    public long Sent, Completed, Responses, Success, NoError, NxDomain, ServFail, Refused, OtherRcode;
    public long Timeouts, NetErrors, OtherErrors, Truncated, BytesIn;
    public readonly LatencyHistogram Latency = new();

    public void Add(in QueryResult r, bool nxIsSuccess)
    {
        Completed++;
        switch (r.Outcome)
        {
            case QueryOutcome.Response:
                Responses++;
                BytesIn += r.ResponseBytes;
                Latency.Record(r.LatencyUs);
                if (r.Truncated) Truncated++;
                switch (r.Rcode)
                {
                    case DnsRcodes.NoError: NoError++; break;
                    case DnsRcodes.NxDomain: NxDomain++; break;
                    case DnsRcodes.ServFail: ServFail++; break;
                    case DnsRcodes.Refused: Refused++; break;
                    default: OtherRcode++; break;
                }
                if (r.IsSuccess(nxIsSuccess)) Success++;
                break;
            case QueryOutcome.Timeout: Timeouts++; break;
            case QueryOutcome.NetworkError: NetErrors++; break;
            default: OtherErrors++; break;
        }
    }

    public void Add(IntervalAccumulator o)
    {
        Sent += o.Sent; Completed += o.Completed; Responses += o.Responses; Success += o.Success;
        NoError += o.NoError; NxDomain += o.NxDomain; ServFail += o.ServFail; Refused += o.Refused; OtherRcode += o.OtherRcode;
        Timeouts += o.Timeouts; NetErrors += o.NetErrors; OtherErrors += o.OtherErrors; Truncated += o.Truncated; BytesIn += o.BytesIn;
        Latency.Add(o.Latency);
    }

    public void Reset()
    {
        Sent = Completed = Responses = Success = NoError = NxDomain = ServFail = Refused = OtherRcode = 0;
        Timeouts = NetErrors = OtherErrors = Truncated = BytesIn = 0;
        Latency.Reset();
    }

    public IntervalStats Freeze(long index, DateTime startLocal, double seconds, HealthState health) => new()
    {
        Index = index,
        StartLocal = startLocal,
        Seconds = seconds,
        Sent = Sent, Completed = Completed, Responses = Responses, Success = Success,
        NoError = NoError, NxDomain = NxDomain, ServFail = ServFail, Refused = Refused, OtherRcode = OtherRcode,
        Timeouts = Timeouts, NetErrors = NetErrors, OtherErrors = OtherErrors, Truncated = Truncated, BytesIn = BytesIn,
        LatMinMs = Latency.MinMs, LatAvgMs = Latency.AvgMs,
        LatP50Ms = Latency.PercentileMs(50), LatP90Ms = Latency.PercentileMs(90),
        LatP95Ms = Latency.PercentileMs(95), LatP99Ms = Latency.PercentileMs(99), LatMaxMs = Latency.MaxMs,
        Health = health,
    };
}

/// <summary>Statistiche congelate di un intervallo chiuso (immutabile, condivisibile con la UI).</summary>
public sealed class IntervalStats
{
    public long Index { get; init; }
    public DateTime StartLocal { get; init; }
    public double Seconds { get; init; }
    public long Sent { get; init; }
    public long Completed { get; init; }
    public long Responses { get; init; }
    public long Success { get; init; }
    public long NoError { get; init; }
    public long NxDomain { get; init; }
    public long ServFail { get; init; }
    public long Refused { get; init; }
    public long OtherRcode { get; init; }
    public long Timeouts { get; init; }
    public long NetErrors { get; init; }
    public long OtherErrors { get; init; }
    public long Truncated { get; init; }
    public long BytesIn { get; init; }
    public double LatMinMs { get; init; }
    public double LatAvgMs { get; init; }
    public double LatP50Ms { get; init; }
    public double LatP90Ms { get; init; }
    public double LatP95Ms { get; init; }
    public double LatP99Ms { get; init; }
    public double LatMaxMs { get; init; }
    public HealthState Health { get; init; }

    public long Failures => Completed - Success;
    public double SuccessPct => Completed == 0 ? double.NaN : 100.0 * Success / Completed;
    public double FailurePct => Completed == 0 ? double.NaN : 100.0 * (Completed - Success) / Completed;
    public double TimeoutPct => Completed == 0 ? double.NaN : 100.0 * Timeouts / Completed;
    public double SentQps => Seconds <= 0 ? 0 : Sent / Seconds;
    public double CompletedQps => Seconds <= 0 ? 0 : Completed / Seconds;
    public double ResponseQps => Seconds <= 0 ? 0 : Responses / Seconds;

    /// <summary>Etichetta stabile dello stato, scritta nei CSV (colonna health) e in summary.json: non va tradotta.</summary>
    public static string HealthLabel(HealthState h) => h switch
    {
        HealthState.Ok => "OK",
        HealthState.Degraded => "DEGRADATO",
        HealthState.Down => "NON DISPONIBILE",
        _ => "NESSUN DATO",
    };

    /// <summary>Etichetta dello stato da mostrare all'utente, nella lingua attiva.</summary>
    public static string HealthDisplay(HealthState h) => h switch
    {
        HealthState.Ok => L.T("OK"),
        HealthState.Degraded => L.T("DEGRADATO"),
        HealthState.Down => L.T("NON DISPONIBILE"),
        _ => L.T("NESSUN DATO"),
    };

    /// <summary>Traduce per la visualizzazione un'etichetta di stato letta dai file (vedi <see cref="HealthLabel"/>).</summary>
    public static string HealthDisplay(string label) => label switch
    {
        "OK" => HealthDisplay(HealthState.Ok),
        "DEGRADATO" => HealthDisplay(HealthState.Degraded),
        "NON DISPONIBILE" => HealthDisplay(HealthState.Down),
        "NESSUN DATO" => HealthDisplay(HealthState.NoData),
        _ => label,
    };
}

/// <summary>Statistiche cumulative per singola voce dell'elenco query.</summary>
public sealed class TemplateStats
{
    public string Query { get; set; } = "";
    public long Completed { get; set; }
    public long Success { get; set; }
    public long Timeouts { get; set; }
    public long Errors { get; set; }
    public long Authoritative { get; set; }
    public long LatSumUs { get; set; }
    public long Responses { get; set; }
    public Dictionary<string, long> Rcodes { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore] public double SuccessPct => Completed == 0 ? double.NaN : 100.0 * Success / Completed;
    [System.Text.Json.Serialization.JsonIgnore] public double AvgMs => Responses == 0 ? double.NaN : LatSumUs / (double)Responses / 1000.0;
    [System.Text.Json.Serialization.JsonIgnore] public double AaPct => Responses == 0 ? double.NaN : 100.0 * Authoritative / Responses;
}
