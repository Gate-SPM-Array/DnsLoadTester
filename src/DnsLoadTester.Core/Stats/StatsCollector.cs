using System.Diagnostics;
using DnsLoadTester.Core.Engine;
using DnsLoadTester.Core.Output;

namespace DnsLoadTester.Core.Stats;

/// <summary>Istantanea per l'interfaccia, ricalcolata a ogni secondo chiuso (immutabile).</summary>
public sealed class LiveSnapshot
{
    public double ElapsedSeconds { get; init; }
    public IntervalStats Total { get; init; } = null!;
    public IntervalStats? LastSecond { get; init; }
    public IntervalStats? Window { get; init; }
    public HealthState Health { get; init; }
    public DateTime HealthSinceLocal { get; init; }
    public int Incidents { get; init; }
    public Dictionary<string, long> Outcomes { get; init; } = [];
    public Dictionary<string, long> HealthSeconds { get; init; } = [];
    public TemplateStats[] Templates { get; init; } = [];
    public QueryResult[] RecentFailures { get; init; } = [];
    public long StrayResponses { get; init; }
    public long ConnectionsOpened { get; init; }
    public long Authoritative { get; init; }
    public long QueryLogLines { get; init; }
    public int QueryLogFiles { get; init; }
    public long SchedulerRebases { get; init; }
    public double PeakResponseQps { get; init; }
    public long PendingResults { get; init; }
}

/// <summary>
/// Raccoglie i risultati (dal solo thread consumatore), chiude gli intervalli da 1 s e 1 min,
/// valuta lo stato di salute del server, scrive aggregati, log ed eventi.
/// </summary>
public sealed class StatsCollector : IDisposable
{
    private readonly TestConfig _cfg;
    private readonly long _startTs;
    private readonly DateTime _startUtc;
    private readonly DateTime _startLocal;
    private readonly EventLog _events;
    private readonly IntervalWriter _w1s, _w1m;
    private readonly QueryLogWriter? _qlog;
    private readonly Func<long> _sentTotal;
    private readonly string _summaryPath;

    private readonly IntervalAccumulator _sec = new(), _min = new(), _total = new();
    private long _secIndex;
    private long _sentAtLastClose;
    private long _authoritative;
    private double _peakQps;

    // Finestra mobile per la valutazione di salute.
    private readonly IntervalAccumulator[] _ring;
    private int _ringPos, _ringFill;
    private HealthState _health = HealthState.NoData;
    private DateTime _healthSince;
    private int _incidents;
    private int _betterStreak;
    private readonly Dictionary<string, long> _healthSeconds = new()
    {
        ["OK"] = 0, ["DEGRADATO"] = 0, ["NON DISPONIBILE"] = 0, ["NESSUN DATO"] = 0,
    };

    private readonly Dictionary<string, long> _outcomes = [];
    private readonly Dictionary<string, long> _errorDetails = [];
    private readonly TemplateStats[] _templates;
    private readonly Queue<QueryResult> _recentFailures = new();
    private const int MaxRecentFailures = 300;

    private readonly List<IntervalStats> _seconds = [];
    private long _secondsOffset;                 // indice assoluto del primo elemento di _seconds
    private const int KeepSecondsInMemory = 4 * 3600;
    private readonly List<IntervalStats> _minutes = [];
    private readonly object _seriesLock = new();
    private DateTime _lastSummaryWrite = DateTime.MinValue;
    private volatile bool _summaryRequested;
    private event Action? SummaryWritten;

    /// <summary>Forza la riscrittura di summary.json al prossimo ciclo (per un report parziale a test in corso).</summary>
    public Task FlushSummaryAsync(TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler() { SummaryWritten -= Handler; tcs.TrySetResult(); }
        SummaryWritten += Handler;
        _summaryRequested = true;
        return Task.WhenAny(tcs.Task, Task.Delay(timeout));
    }

    public volatile LiveSnapshot? Snapshot;
    public Func<long>? StrayProvider, ConnectionsProvider, RebaseProvider, PendingProvider;

    public StatsCollector(TestConfig cfg, QuerySet queries, string sessionDir, CsvFormat csv, EventLog events,
        long startTs, DateTime startUtc, Func<long> sentTotal)
    {
        _cfg = cfg;
        _startTs = startTs;
        _startUtc = startUtc;
        _startLocal = startUtc.ToLocalTime();
        _healthSince = _startLocal;
        _events = events;
        _sentTotal = sentTotal;
        _w1s = new IntervalWriter(Path.Combine(sessionDir, SessionFiles.Intervals1s), csv);
        _w1m = new IntervalWriter(Path.Combine(sessionDir, SessionFiles.Intervals1m), csv);
        if (cfg.QueryLog != QueryLogMode.Off) _qlog = new QueryLogWriter(sessionDir, csv, cfg.QueryLogRotateMb);
        _summaryPath = Path.Combine(sessionDir, SessionFiles.SummaryJson);
        _ring = new IntervalAccumulator[Math.Clamp(cfg.HealthWindowSeconds, 1, 300)];
        for (int i = 0; i < _ring.Length; i++) _ring[i] = new IntervalAccumulator();
        _templates = queries.Items.Select(t => new TemplateStats { Query = t.Display }).ToArray();
    }

    private long SecondOf(long ts) => Math.Max(0, (ts - _startTs) / Stopwatch.Frequency);

    // ------------------------------------------------------------------ input

    public void Process(in QueryResult r)
    {
        long idx = SecondOf(r.CompletedTs);
        if (idx > _secIndex) CloseUntil(idx);

        bool nx = _cfg.NxDomainIsSuccess;
        _sec.Add(r, nx);
        if (r.IsResponse && r.Authoritative) _authoritative++;

        var label = r.OutcomeLabel;
        _outcomes[label] = _outcomes.GetValueOrDefault(label) + 1;

        var ts = _templates[r.TemplateIndex];
        ts.Completed++;
        if (r.IsResponse)
        {
            ts.Responses++;
            ts.LatSumUs += r.LatencyUs;
            if (r.Authoritative) ts.Authoritative++;
            ts.Rcodes[label] = ts.Rcodes.GetValueOrDefault(label) + 1;
        }
        else if (r.Outcome == QueryOutcome.Timeout) ts.Timeouts++;
        else ts.Errors++;

        bool success = r.IsSuccess(nx);
        if (success) ts.Success++;
        else
        {
            if (r.Detail != null)
            {
                var key = $"{label}: {r.Detail}";
                if (_errorDetails.Count < 500 || _errorDetails.ContainsKey(key))
                    _errorDetails[key] = _errorDetails.GetValueOrDefault(key) + 1;
            }
            _recentFailures.Enqueue(r);
            if (_recentFailures.Count > MaxRecentFailures) _recentFailures.Dequeue();
        }

        if (_qlog != null && (_cfg.QueryLog == QueryLogMode.All || !success))
            _qlog.Write(r, _startUtc);
    }

    /// <summary>Chiamato periodicamente dal consumatore: chiude i secondi trascorsi anche senza traffico.</summary>
    public void Tick()
    {
        long idx = SecondOf(Stopwatch.GetTimestamp());
        if (idx > _secIndex) CloseUntil(idx);
        if (_summaryRequested || (DateTime.Now - _lastSummaryWrite).TotalSeconds >= 30)
        {
            WriteSummary("In corso");
            _summaryRequested = false;
            SummaryWritten?.Invoke();
        }
    }

    // ------------------------------------------------------------------ chiusura intervalli

    private void CloseUntil(long idx)
    {
        // Dopo una lunga sospensione del PC non si generano migliaia di secondi vuoti uno per uno.
        if (idx - _secIndex > 3600)
        {
            _events.Event("WARN", "Sistema", L.F("Salto temporale di {0} s (PC sospeso o bloccato?): intervalli vuoti non registrati.", idx - _secIndex));
            CloseSecond(1.0);
            _secIndex = idx;
            return;
        }
        while (_secIndex < idx) CloseSecond(1.0);
    }

    private void CloseSecond(double seconds)
    {
        long sentNow = _sentTotal();
        _sec.Sent = sentNow - _sentAtLastClose;
        _sentAtLastClose = sentNow;

        // finestra di salute
        _ring[_ringPos].Reset();
        _ring[_ringPos].Add(_sec);
        _ringPos = (_ringPos + 1) % _ring.Length;
        if (_ringFill < _ring.Length) _ringFill++;
        var window = new IntervalAccumulator();
        for (int i = 0; i < _ringFill; i++) window.Add(_ring[i]);
        var windowStats = window.Freeze(-1, DateTime.Now.AddSeconds(-_ringFill), _ringFill, _health);
        EvaluateHealth(window, windowStats);

        var secStart = _startLocal.AddSeconds(_secIndex);
        var st = _sec.Freeze(_secIndex, secStart, seconds, _health);
        _healthSeconds[IntervalStats.HealthLabel(_health)] += 1;
        if (st.ResponseQps > _peakQps) _peakQps = st.ResponseQps;
        _w1s.Write(st, _secIndex);
        lock (_seriesLock)
        {
            _seconds.Add(st);
            if (_seconds.Count > KeepSecondsInMemory + 600)
            {
                _seconds.RemoveRange(0, 600);
                _secondsOffset += 600;
            }
        }

        _total.Add(_sec);
        _min.Add(_sec);
        _sec.Reset();
        _secIndex++;

        if (_secIndex % 60 == 0) CloseMinute(60);
        _qlog?.Flush();
        PublishSnapshot(st, windowStats);
    }

    private void CloseMinute(double seconds)
    {
        long minIdx = (_secIndex - 1) / 60;
        var start = _startLocal.AddSeconds(minIdx * 60);
        var worst = WorstHealthInMinute(minIdx);
        var st = _min.Freeze(minIdx, start, seconds, worst);
        _w1m.Write(st, minIdx * 60);
        lock (_seriesLock) _minutes.Add(st);
        _min.Reset();
    }

    private HealthState WorstHealthInMinute(long minIdx)
    {
        var worst = HealthState.NoData;
        lock (_seriesLock)
        {
            for (int i = _seconds.Count - 1; i >= 0 && _seconds[i].Index >= minIdx * 60; i--)
                if (_seconds[i].Health > worst) worst = _seconds[i].Health;
        }
        return worst;
    }

    private void EvaluateHealth(IntervalAccumulator w, IntervalStats ws)
    {
        HealthState next;
        string reason;
        if (w.Completed == 0)
        {
            next = w.Sent > 0 ? HealthState.Down : HealthState.NoData;
            reason = w.Sent > 0 ? L.T("nessuna risposta") : L.T("nessun traffico");
        }
        else
        {
            double succ = ws.SuccessPct;
            double p95 = ws.LatP95Ms;
            reason = double.IsNaN(p95)
                ? L.F("successo {0:F2}%, timeout {1:F2}%, p95 n/d", succ, ws.TimeoutPct)
                : L.F("successo {0:F2}%, timeout {1:F2}%, p95 {2:F1} ms", succ, ws.TimeoutPct, p95);
            if (succ < _cfg.DownSuccessPct) next = HealthState.Down;
            else if (succ < _cfg.DegradedSuccessPct || (!double.IsNaN(p95) && p95 > _cfg.DegradedP95Ms)) next = HealthState.Degraded;
            else next = HealthState.Ok;
        }
        if (next == _health) { _betterStreak = 0; return; }

        // Isteresi: il peggioramento è immediato, il miglioramento deve persistere per l'intera finestra.
        bool better = _health != HealthState.NoData && next < _health && next != HealthState.NoData;
        if (better)
        {
            if (++_betterStreak < _ring.Length) return;
        }
        _betterStreak = 0;

        var now = DateTime.Now;
        var prevDuration = now - _healthSince;
        string prevLabel = IntervalStats.HealthDisplay(_health);
        string nextLabel = IntervalStats.HealthDisplay(next);
        string level = next switch { HealthState.Down => "ERROR", HealthState.Degraded => "WARN", _ => "INFO" };
        if ((next == HealthState.Degraded || next == HealthState.Down) && (_health == HealthState.Ok || _health == HealthState.NoData))
            _incidents++;
        _events.Event(level, "Salute", L.F("{0} → {1} (finestra {2} s: {3}); stato precedente durato {4}", prevLabel, nextLabel, _ringFill, reason, FormatDuration(prevDuration)));
        _health = next;
        _healthSince = now;
    }

    public static string FormatDuration(TimeSpan d) =>
        d.TotalDays >= 1 ? L.F("{0}g {1}", (int)d.TotalDays, d.ToString(@"hh\:mm\:ss")) : d.ToString(@"hh\:mm\:ss");

    // ------------------------------------------------------------------ output per UI

    private void PublishSnapshot(IntervalStats last, IntervalStats window)
    {
        double elapsed = _secIndex;
        var total = _total.Freeze(0, _startLocal, elapsed, _health);
        Snapshot = new LiveSnapshot
        {
            ElapsedSeconds = elapsed,
            Total = total,
            LastSecond = last,
            Window = window,
            Health = _health,
            HealthSinceLocal = _healthSince,
            Incidents = _incidents,
            Outcomes = new Dictionary<string, long>(_outcomes),
            HealthSeconds = new Dictionary<string, long>(_healthSeconds),
            Templates = _templates.Select(t => new TemplateStats
            {
                Query = t.Query, Completed = t.Completed, Success = t.Success, Timeouts = t.Timeouts, Errors = t.Errors,
                Authoritative = t.Authoritative, LatSumUs = t.LatSumUs, Responses = t.Responses,
            }).ToArray(),
            RecentFailures = _recentFailures.Reverse().Take(200).ToArray(),
            StrayResponses = StrayProvider?.Invoke() ?? 0,
            ConnectionsOpened = ConnectionsProvider?.Invoke() ?? 0,
            Authoritative = _authoritative,
            QueryLogLines = _qlog?.Lines ?? 0,
            QueryLogFiles = _qlog?.FileCount ?? 0,
            SchedulerRebases = RebaseProvider?.Invoke() ?? 0,
            PeakResponseQps = _peakQps,
            PendingResults = PendingProvider?.Invoke() ?? 0,
        };
    }

    /// <summary>
    /// Copia dei secondi chiusi con indice assoluto ≥ <paramref name="fromIndex"/> (aggiornamenti incrementali dei grafici).
    /// In memoria restano solo le ultime ~4 ore: lo storico completo è in intervals_1s.csv.
    /// </summary>
    public List<IntervalStats> GetSeconds(long fromIndex)
    {
        lock (_seriesLock)
        {
            int start = (int)Math.Max(0, fromIndex - _secondsOffset);
            return start >= _seconds.Count ? [] : _seconds.GetRange(start, _seconds.Count - start);
        }
    }

    public List<IntervalStats> GetMinutes(int from)
    {
        lock (_seriesLock)
            return from >= _minutes.Count ? [] : _minutes.GetRange(from, _minutes.Count - from);
    }

    // ------------------------------------------------------------------ chiusura e riepilogo

    public void Finish(string status)
    {
        // secondo parziale finale (durata reale) e minuto parziale
        double frac = (Stopwatch.GetTimestamp() - _startTs) / (double)Stopwatch.Frequency - _secIndex;
        if (_sec.Completed > 0 || frac > 0.05) CloseSecond(Math.Clamp(frac, 0.001, 1.0));
        if (_secIndex % 60 != 0) CloseMinute(_secIndex % 60);
        _qlog?.Dispose();
        WriteSummary(status, DateTime.Now);
    }

    public SessionSummary BuildSummary(string status, DateTime? end = null)
    {
        var h = _total.Latency;
        return new SessionSummary
        {
            Status = status,
            StartLocal = _startLocal,
            LastUpdateLocal = DateTime.Now,
            EndLocal = end,
            ElapsedSeconds = _secIndex,
            Sent = _sentTotal(),
            Completed = _total.Completed,
            Responses = _total.Responses,
            Success = _total.Success,
            Timeouts = _total.Timeouts,
            NetErrors = _total.NetErrors,
            OtherErrors = _total.OtherErrors,
            Truncated = _total.Truncated,
            Authoritative = _authoritative,
            StrayResponses = StrayProvider?.Invoke() ?? 0,
            ConnectionsOpened = ConnectionsProvider?.Invoke() ?? 0,
            BytesIn = _total.BytesIn,
            QueryLogLines = _qlog?.Lines ?? 0,
            QueryLogFiles = _qlog?.FileCount ?? 0,
            PeakResponseQps = _peakQps,
            Outcomes = new Dictionary<string, long>(_outcomes),
            ErrorDetails = _errorDetails.OrderByDescending(kv => kv.Value).Take(50).ToDictionary(kv => kv.Key, kv => kv.Value),
            HealthSeconds = new Dictionary<string, long>(_healthSeconds),
            HealthIncidents = _incidents,
            LatCount = h.Count,
            LatSumUs = h.SumUs,
            LatMinUs = h.Count > 0 ? h.MinUs : 0,
            LatMaxUs = h.MaxUs,
            LatHistogram = h.ToSparse(),
            Templates = _templates.ToList(),
        };
    }

    private void WriteSummary(string status, DateTime? end = null)
    {
        try
        {
            SessionFiles.WriteAllTextAtomic(_summaryPath, BuildSummary(status, end).ToJson());
            _lastSummaryWrite = DateTime.Now;
        }
        catch (Exception ex)
        {
            _events.App(L.F("Errore nella scrittura di summary.json: {0}", ex.Message));
            _lastSummaryWrite = DateTime.Now;
        }
    }

    public void Dispose()
    {
        _w1s.Dispose();
        _w1m.Dispose();
        _qlog?.Dispose();
    }
}
