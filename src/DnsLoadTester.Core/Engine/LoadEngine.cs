using System.Diagnostics;
using System.Threading.Channels;
using DnsLoadTester.Core.Output;
using DnsLoadTester.Core.Report;
using DnsLoadTester.Core.Stats;
using DnsLoadTester.Core.Transport;

namespace DnsLoadTester.Core.Engine;

public enum EngineState { Idle, Starting, Running, Stopping, Finished, Failed }

/// <summary>
/// Orchestratore di una sessione di test: crea la cartella di sessione, avvia i worker,
/// raccoglie i risultati su un unico thread consumatore (nessun lock sul percorso caldo),
/// alla fine scrive riepilogo e report.
/// </summary>
public sealed class LoadEngine
{
    private readonly TestConfig _cfg;
    private readonly string _appVersion;
    private readonly CancellationTokenSource _stopCts = new();
    private Channel<QueryResult> _channel = null!;
    private TransportContext? _ctx;
    private QuerySet _queries = null!;
    private RateScheduler _scheduler = null!;
    private readonly List<QueryRunner> _runners = [];
    private long _sent;
    private long _pending;
    private string _stopReason = "";

    public EngineState State { get; private set; } = EngineState.Idle;
    public string SessionFolder { get; private set; } = "";
    public string? ReportPath { get; private set; }
    public StatsCollector? Stats { get; private set; }
    public EventLog? Events { get; private set; }
    public DateTime StartLocal { get; private set; }
    public Task Completion { get; private set; } = Task.CompletedTask;
    public TestConfig Config => _cfg;
    public string ResolvedTarget => _ctx?.ResolvedDescription ?? "";
    public IReadOnlyList<string> QueryWarnings { get; private set; } = [];

    /// <summary>Notifica eventi (avvio, salute, errori, fine). Può arrivare da thread diversi dalla UI.</summary>
    public event Action<SessionEvent>? EventRaised;
    public event Action<EngineState>? StateChanged;

    public LoadEngine(TestConfig cfg, string appVersion)
    {
        _cfg = cfg.Clone();
        _appVersion = appVersion;
    }

    private void SetState(EngineState s) { State = s; StateChanged?.Invoke(s); }

    /// <summary>Prepara la sessione e avvia il test. Lancia eccezione se la configurazione non è valida o la destinazione non è raggiungibile.</summary>
    public async Task StartAsync()
    {
        if (State != EngineState.Idle) throw new InvalidOperationException(L.T("Motore già avviato."));
        var errors = _cfg.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));

        SetState(EngineState.Starting);
        try
        {
            _queries = QuerySet.Parse(_cfg.Queries, _cfg.DefaultQueryType, _cfg.Order, out var warnings);
            QueryWarnings = warnings;
            if (_queries.Items.Count == 0) throw new ArgumentException(L.T("Nessuna query valida nell'elenco."));

            using (var resolveCts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                _ctx = await TransportContext.CreateAsync(_cfg, resolveCts.Token).ConfigureAwait(false);

            var startUtc = DateTime.UtcNow;
            StartLocal = startUtc.ToLocalTime();
            SessionFolder = SessionFiles.CreateSessionFolder(_cfg, StartLocal);
            var csv = new CsvFormat(_cfg.CsvDialect);
            Events = new EventLog(SessionFolder, csv);
            Events.EventAdded += e => EventRaised?.Invoke(e);

            var info = new SessionInfo
            {
                AppVersion = _appVersion, StartLocal = StartLocal, ResolvedTarget = _ctx.ResolvedDescription,
                CsvDialect = _cfg.CsvDialect, Config = _cfg,
            };
            SessionFiles.WriteAllTextAtomic(Path.Combine(SessionFolder, SessionFiles.SessionJson),
                System.Text.Json.JsonSerializer.Serialize(info, TestConfig.JsonOptions));

            for (int i = 0; i < _cfg.Concurrency; i++) _runners.Add(new QueryRunner(_cfg, _ctx, i));

            long startTs = Stopwatch.GetTimestamp();
            _scheduler = new RateScheduler(_cfg.TargetQps, _cfg.RampUpSeconds, startTs);
            _channel = Channel.CreateUnbounded<QueryResult>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            Stats = new StatsCollector(_cfg, _queries, SessionFolder, csv, Events, startTs, startUtc, () => Interlocked.Read(ref _sent))
            {
                StrayProvider = () => _runners.Sum(r => Interlocked.Read(ref r.Transport.StrayResponses)),
                ConnectionsProvider = () => _runners.Sum(r => Interlocked.Read(ref r.Transport.ConnectionsOpened)),
                RebaseProvider = () => _scheduler.BehindEvents,
                PendingProvider = () => Interlocked.Read(ref _pending),
            };

            Events.Event("INFO", "Sessione", L.F("Avvio test v{0}: {1} (risolto {2}), {3} query, worker {4}, QPS {5}, durata {6}, timeout {7} ms",
                _appVersion, _cfg.TargetDescription, _ctx.ResolvedDescription, _queries.Items.Count, _cfg.Concurrency,
                _cfg.TargetQps > 0 ? _cfg.TargetQps.ToString() : L.T("illimitato"),
                _cfg.DurationSeconds > 0 ? StatsCollector.FormatDuration(TimeSpan.FromSeconds(_cfg.DurationSeconds)) : L.T("fino ad arresto manuale"),
                _cfg.TimeoutMs));
            foreach (var w in warnings) Events.Event("WARN", "Query", w);

            if (_cfg.DurationSeconds > 0) _stopCts.CancelAfter(TimeSpan.FromSeconds(_cfg.DurationSeconds));
            Completion = Task.Run(RunAsync);
            SetState(EngineState.Running);
        }
        catch
        {
            SetState(EngineState.Failed);
            await CleanupAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Stop(string? reason = null)
    {
        if (State != EngineState.Running) return;
        reason ??= L.T("Interrotto dall'utente");
        _stopReason = reason;
        SetState(EngineState.Stopping);
        Events?.Event("INFO", "Sessione", L.F("Richiesta di arresto: {0}. Attendo le query in corso (max {1} ms).", reason, _cfg.TimeoutMs));
        _stopCts.Cancel();
    }

    private async Task RunAsync()
    {
        var consumer = Task.Factory.StartNew(ConsumeLoop, TaskCreationOptions.LongRunning).Unwrap();
        var workers = _runners.Select(r => Task.Run(() => WorkerLoop(r))).ToArray();
        string status;
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
            status = _stopCts.IsCancellationRequested && State == EngineState.Stopping ? "Interrotto" : "Completato";
        }
        catch (Exception ex)
        {
            Events?.Event("ERROR", "Motore", L.F("Errore inatteso nei worker: {0}", ex));
            status = "Errore";
        }
        _channel.Writer.TryComplete();
        try { await consumer.ConfigureAwait(false); }
        catch (Exception ex) { Events?.Event("ERROR", "Motore", L.F("Errore nel thread di raccolta: {0}", ex)); status = "Errore"; }

        try
        {
            Stats!.Finish(status);
            var snap = Stats.Snapshot;
            Events!.Event("INFO", "Sessione", L.F("Fine test ({0}): {1:N0} query completate, successo {2:F3}%, p95 {3:F1} ms. Motivo: {4}.",
                Labels.SessionStatus(status), snap?.Total.Completed ?? 0, snap?.Total.SuccessPct ?? double.NaN, snap?.Total.LatP95Ms ?? double.NaN,
                status == "Completato" ? L.T("durata raggiunta") : _stopReason));
            Stats.Dispose();
            ReportPath = ReportGenerator.Generate(SessionFolder);
            Events.Event("INFO", "Report", L.F("Report generato: {0}", Path.GetFileName(ReportPath)));
        }
        catch (Exception ex)
        {
            Events?.Event("ERROR", "Report", L.F("Generazione del report non riuscita: {0}", ex.Message));
        }
        await CleanupAsync().ConfigureAwait(false);
        SetState(status == "Errore" ? EngineState.Failed : EngineState.Finished);
        Events?.Dispose();
    }

    private async Task WorkerLoop(QueryRunner runner)
    {
        var stop = _stopCts.Token;
        var writer = _channel.Writer;
        bool unlimited = _scheduler.Unlimited;
        while (!stop.IsCancellationRequested)
        {
            try { await _scheduler.WaitTurnAsync(stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (stop.IsCancellationRequested) break;

            var t = _queries.Next();
            var r = await runner.RunAsync(t, () => Interlocked.Increment(ref _sent)).ConfigureAwait(false);
            Interlocked.Increment(ref _pending);
            writer.TryWrite(r);
            // In modalità "massimo throughput" evita di saturare la CPU se il server rifiuta subito le connessioni.
            if (unlimited && r.Outcome is QueryOutcome.NetworkError or QueryOutcome.TlsError && r.LatencyUs < 1000)
            {
                try { await Task.Delay(10, stop).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ConsumeLoop()
    {
        var reader = _channel.Reader;
        var stats = Stats!;
        while (true)
        {
            int n = 0;
            while (reader.TryRead(out var r))
            {
                stats.Process(r);
                n++;
            }
            if (n > 0) Interlocked.Add(ref _pending, -n);
            stats.Tick();
            if (reader.Completion.IsCompleted) break;
            try { await Task.Delay(50).ConfigureAwait(false); } catch { }
        }
        while (reader.TryRead(out var r)) stats.Process(r);
    }

    private async Task CleanupAsync()
    {
        foreach (var r in _runners)
        {
            try { await r.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _ctx?.Dispose();
    }

    // ------------------------------------------------------------------ verifica preliminare

    /// <summary>Invia una volta ciascuna query (max <paramref name="maxQueries"/>) per verificare raggiungibilità e risposte.</summary>
    public static async Task<(string Resolved, List<QueryResult> Results, List<string> Warnings)> ProbeAsync(TestConfig cfg, int maxQueries, CancellationToken ct)
    {
        var c = cfg.Clone();
        c.Concurrency = 1;
        var qs = QuerySet.Parse(c.Queries, c.DefaultQueryType, QueryOrder.RoundRobin, out var warnings);
        if (qs.Items.Count == 0) throw new ArgumentException(L.T("Nessuna query valida nell'elenco."));
        using var ctx = await TransportContext.CreateAsync(c, ct).ConfigureAwait(false);
        await using var runner = new QueryRunner(c, ctx, 0);
        var results = new List<QueryResult>();
        foreach (var t in qs.Items.Take(maxQueries))
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await runner.RunAsync(t).ConfigureAwait(false));
        }
        return (ctx.ResolvedDescription, results, warnings);
    }
}
