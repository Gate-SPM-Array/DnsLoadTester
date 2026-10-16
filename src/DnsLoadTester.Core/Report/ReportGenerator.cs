using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DnsLoadTester.Core.Dns;
using DnsLoadTester.Core.Engine;
using DnsLoadTester.Core.Output;
using DnsLoadTester.Core.Stats;
using static DnsLoadTester.Core.Report.SvgChart;

namespace DnsLoadTester.Core.Report;

/// <summary>
/// Genera il report HTML finale leggendo solo i file della cartella di sessione:
/// funziona quindi anche su sessioni interrotte da crash o chiusura del programma.
/// </summary>
public static class ReportGenerator
{
    private static CultureInfo C => L.Culture;
    private const int MaxPoints = 1500;

    public static string Generate(string sessionDir, bool partial = false)
    {
        var infoPath = Path.Combine(sessionDir, SessionFiles.SessionJson);
        if (!File.Exists(infoPath)) throw new FileNotFoundException(L.T("session.json non trovato: non è una cartella di sessione valida."), infoPath);
        var info = JsonSerializer.Deserialize<SessionInfo>(ReadShared(infoPath), TestConfig.JsonOptions)!;
        var csv = new CsvFormat(info.CsvDialect);
        var cfg = info.Config;

        SessionSummary? summary = null;
        var sumPath = Path.Combine(sessionDir, SessionFiles.SummaryJson);
        if (File.Exists(sumPath))
            summary = JsonSerializer.Deserialize<SessionSummary>(ReadShared(sumPath), TestConfig.JsonOptions);

        var minutes = IntervalWriter.Read(Path.Combine(sessionDir, SessionFiles.Intervals1m), csv, 60);
        var seconds = IntervalWriter.Read(Path.Combine(sessionDir, SessionFiles.Intervals1s), csv);
        var events = EventLog.Read(Path.Combine(sessionDir, SessionFiles.EventsCsv), csv);

        // Se summary.json manca (crash prima della prima scrittura) si ricostruiscono i totali dagli intervalli.
        summary ??= RebuildSummary(seconds, info);
        string status = partial
            ? L.T("Test in corso – report parziale")
            : summary.Status == "In corso" && seconds.Count > 0
                ? L.F("Interrotto inaspettatamente (dati fino al {0:dd/MM/yyyy HH:mm:ss})", seconds[^1].StartLocal)
                : Labels.SessionStatus(summary.Status);

        // Serie per i grafici: secondi per test brevi, minuti per test lunghi.
        bool useSeconds = seconds.Count <= 2 * 3600 || minutes.Count < 3;
        var src = useSeconds ? seconds : minutes;
        // L'ultimo intervallo parziale contiene solo le risposte "in coda" dopo lo stop: falserebbe i grafici.
        if (src.Count > 2 && src[^1].Sent == 0 && src[^1].Seconds < (useSeconds ? 1 : 60)) src = src.GetRange(0, src.Count - 1);
        var series = Downsample(src, MaxPoints);
        var step = series.Count > 1 ? series[1].StartLocal - series[0].StartLocal : TimeSpan.FromSeconds(1);

        var html = Build(info, cfg, summary, status, series, step, seconds, events, useSeconds);
        var path = NextReportPath(sessionDir);
        File.WriteAllText(path, html, new UTF8Encoding(false));
        return path;
    }

    /// <summary>Lettura che non blocca la riscrittura atomica dei JSON da parte del test in corso.</summary>
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static string NextReportPath(string dir)
    {
        var rx = new Regex(@"^report_v(\d+)\.(\d+)\.html$", RegexOptions.IgnoreCase);
        int minor = -1;
        foreach (var f in Directory.EnumerateFiles(dir, "report_v*.html"))
        {
            var m = rx.Match(Path.GetFileName(f));
            if (m.Success && m.Groups[1].Value == "1") minor = Math.Max(minor, int.Parse(m.Groups[2].Value));
        }
        return Path.Combine(dir, $"{SessionFiles.ReportPrefix}1.{minor + 1}.html");
    }

    private static SessionSummary RebuildSummary(List<IntervalStats> secs, SessionInfo info) => new()
    {
        Status = "Ricostruito dagli intervalli",
        StartLocal = info.StartLocal,
        ElapsedSeconds = secs.Sum(s => s.Seconds),
        Sent = secs.Sum(s => s.Sent),
        Completed = secs.Sum(s => s.Completed),
        Responses = secs.Sum(s => s.Responses),
        Success = secs.Sum(s => s.Success),
        Timeouts = secs.Sum(s => s.Timeouts),
        NetErrors = secs.Sum(s => s.NetErrors),
        OtherErrors = secs.Sum(s => s.OtherErrors),
        Truncated = secs.Sum(s => s.Truncated),
        PeakResponseQps = secs.Count == 0 ? 0 : secs.Max(s => s.ResponseQps),
        HealthSeconds = secs.GroupBy(s => IntervalStats.HealthLabel(s.Health)).ToDictionary(g => g.Key, g => (long)g.Count()),
        Outcomes = new()
        {
            ["NOERROR"] = secs.Sum(s => s.NoError), ["NXDOMAIN"] = secs.Sum(s => s.NxDomain), ["SERVFAIL"] = secs.Sum(s => s.ServFail),
            ["REFUSED"] = secs.Sum(s => s.Refused), ["ALTRI RCODE"] = secs.Sum(s => s.OtherRcode), ["TIMEOUT"] = secs.Sum(s => s.Timeouts),
            ["NET_ERROR"] = secs.Sum(s => s.NetErrors), ["ALTRI ERRORI"] = secs.Sum(s => s.OtherErrors),
        },
    };

    /// <summary>Riduce la serie a ≤ maxPoints accorpando intervalli consecutivi (QPS medio, p50 medio, p95/p99/max = massimo del gruppo).</summary>
    public static List<IntervalStats> Downsample(List<IntervalStats> src, int maxPoints)
    {
        if (src.Count <= maxPoints) return src;
        int g = (int)Math.Ceiling(src.Count / (double)maxPoints);
        var res = new List<IntervalStats>(src.Count / g + 1);
        for (int i = 0; i < src.Count; i += g)
        {
            var grp = src.GetRange(i, Math.Min(g, src.Count - i));
            double MaxOf(Func<IntervalStats, double> f) { var v = grp.Select(f).Where(x => !double.IsNaN(x)).ToList(); return v.Count == 0 ? double.NaN : v.Max(); }
            double AvgOf(Func<IntervalStats, double> f) { var v = grp.Select(f).Where(x => !double.IsNaN(x)).ToList(); return v.Count == 0 ? double.NaN : v.Average(); }
            double MinOf(Func<IntervalStats, double> f) { var v = grp.Select(f).Where(x => !double.IsNaN(x)).ToList(); return v.Count == 0 ? double.NaN : v.Min(); }
            res.Add(new IntervalStats
            {
                Index = grp[0].Index,
                StartLocal = grp[0].StartLocal,
                Seconds = grp.Sum(x => x.Seconds),
                Sent = grp.Sum(x => x.Sent), Completed = grp.Sum(x => x.Completed), Responses = grp.Sum(x => x.Responses),
                Success = grp.Sum(x => x.Success), NoError = grp.Sum(x => x.NoError), NxDomain = grp.Sum(x => x.NxDomain),
                ServFail = grp.Sum(x => x.ServFail), Refused = grp.Sum(x => x.Refused), OtherRcode = grp.Sum(x => x.OtherRcode),
                Timeouts = grp.Sum(x => x.Timeouts), NetErrors = grp.Sum(x => x.NetErrors), OtherErrors = grp.Sum(x => x.OtherErrors),
                Truncated = grp.Sum(x => x.Truncated), BytesIn = grp.Sum(x => x.BytesIn),
                LatMinMs = MinOf(x => x.LatMinMs), LatAvgMs = AvgOf(x => x.LatAvgMs), LatP50Ms = AvgOf(x => x.LatP50Ms),
                LatP90Ms = MaxOf(x => x.LatP90Ms), LatP95Ms = MaxOf(x => x.LatP95Ms), LatP99Ms = MaxOf(x => x.LatP99Ms), LatMaxMs = MaxOf(x => x.LatMaxMs),
                Health = grp.Max(x => x.Health),
            });
        }
        return res;
    }

    // ------------------------------------------------------------------ HTML

    private static string NA => L.T("n/d");
    private static string N(long v) => v.ToString("N0", C);
    private static string P(double v, int d = 3) => double.IsNaN(v) ? NA : v.ToString("F" + d, C) + "%";
    private static string Ms(double v) => double.IsNaN(v) ? NA : v.ToString(v < 10 ? "F2" : "F1", C) + " ms";
    private static string D(double v, int d = 1) => double.IsNaN(v) ? NA : v.ToString("N" + d, C);

    private static string Build(SessionInfo info, TestConfig cfg, SessionSummary s, string status, List<IntervalStats> series, TimeSpan step,
        List<IntervalStats> seconds, List<SessionEvent> events, bool useSeconds)
    {
        var h = s.GetHistogram();
        double elapsed = Math.Max(s.ElapsedSeconds, 1);
        double successPct = s.Completed == 0 ? double.NaN : 100.0 * s.Success / s.Completed;
        long healthTotal = s.HealthSeconds.Values.Sum();
        long okSec = s.HealthSeconds.GetValueOrDefault("OK");
        long degSec = s.HealthSeconds.GetValueOrDefault("DEGRADATO");
        long downSec = s.HealthSeconds.GetValueOrDefault("NON DISPONIBILE");
        long measured = okSec + degSec + downSec;
        double availPct = measured == 0 ? double.NaN : 100.0 * (okSec + degSec) / measured;
        double okPct = measured == 0 ? double.NaN : 100.0 * okSec / measured;
        string verdictClass = double.IsNaN(successPct) ? "na" : downSec > 0 || successPct < cfg.DownSuccessPct ? "bad" : degSec > 0 || successPct < cfg.DegradedSuccessPct ? "warn" : "good";
        string verdict = verdictClass switch
        {
            "good" => L.T("Il server ha risposto in modo regolare per tutta la durata del test."),
            "warn" => L.T("Il server ha risposto, ma con periodi di degrado (perdita di risposte o latenza oltre soglia)."),
            "bad" => L.T("Il server ha avuto periodi di indisponibilità durante il test."),
            _ => L.T("Dati insufficienti per una valutazione."),
        };
        string target = Esc(TargetShort(cfg));

        var sb = new StringBuilder();
        sb.Append($"<!doctype html><html lang=\"{L.Code}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(L.F("Report test DNS – {0}", target)).Append($"</title><style>{Css}</style></head><body><main>");

        sb.Append("<header><div class=\"brand\">").Append(Esc(L.T("DNS Load Tester · Report di sessione"))).Append("</div>");
        sb.Append("<h1>").Append(L.F("Test di capacità DNS – {0}", target)).Append("</h1>");
        sb.Append("<p class=\"sub\">").Append(Esc(cfg.TargetDescription)).Append(" · ").Append(Esc(L.F("avvio {0:dd/MM/yyyy HH:mm:ss}", s.StartLocal)));
        if (s.EndLocal is { } end) sb.Append(" · ").Append(Esc(L.F("fine {0:dd/MM/yyyy HH:mm:ss}", end)));
        sb.Append(" · ").Append(Esc(L.F("durata {0}", StatsCollector.FormatDuration(TimeSpan.FromSeconds(s.ElapsedSeconds)))));
        sb.Append(" · ").Append(Esc(L.T("esito:"))).Append($" <b>{Esc(status)}</b></p></header>");

        sb.Append($"<section class=\"verdict {verdictClass}\"><b>").Append(Esc(L.T("Sintesi."))).Append("</b> ").Append(Esc(verdict)).Append(' ');
        sb.Append(Esc(cfg.NxDomainIsSuccess
            ? L.F("Su {0} query completate il {1} ha ricevuto una risposta valida (NOERROR o NXDOMAIN); latenza mediana {2}, p99 {3}; {4} episodi di degrado o indisponibilità.",
                N(s.Completed), P(successPct), Ms(h.PercentileMs(50)), Ms(h.PercentileMs(99)), s.HealthIncidents)
            : L.F("Su {0} query completate il {1} ha ricevuto una risposta valida (NOERROR); latenza mediana {2}, p99 {3}; {4} episodi di degrado o indisponibilità.",
                N(s.Completed), P(successPct), Ms(h.PercentileMs(50)), Ms(h.PercentileMs(99)), s.HealthIncidents)));
        sb.Append("</section>");

        // KPI
        sb.Append("<section class=\"kpis\">");
        Kpi(sb, L.T("Query inviate"), N(s.Sent));
        Kpi(sb, L.T("Successo"), P(successPct));
        Kpi(sb, L.T("Disponibilità"), P(availPct, 2), L.F("secondi in OK: {0}", P(okPct, 2)));
        Kpi(sb, L.T("QPS medio (risposte)"), D(s.Responses / elapsed), L.F("picco {0}", D(s.PeakResponseQps)));
        Kpi(sb, L.T("Latenza p50"), Ms(h.PercentileMs(50)));
        Kpi(sb, L.T("Latenza p95"), Ms(h.PercentileMs(95)));
        Kpi(sb, L.T("Latenza p99"), Ms(h.PercentileMs(99)));
        Kpi(sb, L.T("Timeout"), N(s.Timeouts), P(s.Completed == 0 ? double.NaN : 100.0 * s.Timeouts / s.Completed));
        sb.Append("</section>");

        // Grafici
        var xs = series.Select(x => x.StartLocal).ToList();
        string res = useSeconds
            ? (series.Count < seconds.Count - 1 ? L.T("aggregazione di più secondi") : L.T("risoluzione 1 s"))
            : L.T("risoluzione 1 min o superiore");
        sb.Append("<h2>").Append(Esc(L.T("Andamento nel tempo"))).Append($" <span class=\"muted\">({Esc(res)})</span></h2>");
        sb.Append("<div class=\"card\">").Append(HealthStrip(xs, series.Select(x => (int)x.Health).ToList(), step)).Append("</div>");
        sb.Append("<div class=\"card\">").Append(Line(L.T("Throughput"), xs,
        [
            new(L.T("Inviate/s"), "#94a3b8", series.Select(x => x.SentQps).ToArray(), true),
            new(L.T("Risposte/s"), "#2563eb", series.Select(x => x.ResponseQps).ToArray()),
            new(L.T("Successi/s"), "#16a34a", series.Select(x => x.Seconds <= 0 ? 0 : x.Success / x.Seconds).ToArray()),
        ], L.T("query/s"))).Append("</div>");
        sb.Append("<div class=\"card\">").Append(Line(L.T("Latenza"), xs,
        [
            new("p50", "#16a34a", series.Select(x => x.LatP50Ms).ToArray()),
            new("p95", "#f59e0b", series.Select(x => x.LatP95Ms).ToArray()),
            new("p99", "#dc2626", series.Select(x => x.LatP99Ms).ToArray()),
        ], "ms")).Append("</div>");
        sb.Append("<div class=\"card\">").Append(Line(L.T("Fallimenti"), xs,
        [
            new(L.T("% fallite (totale)"), "#dc2626", series.Select(x => x.FailurePct).ToArray()),
            new(L.T("% timeout"), "#7c3aed", series.Select(x => x.TimeoutPct).ToArray()),
        ], "%")).Append("</div>");

        // Distribuzione latenze
        double[] edges = [0, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, double.PositiveInfinity];
        string half = 0.5.ToString("0.0", C);
        string[] labels = ["<1", "1-2", "2-5", "5-10", "10-20", "20-50", "50-100", "100-200", "200-500", half + "-1s", "1-2s", ">2s"];
        var bins = new double[labels.Length];
        var raw = h.RawCounts;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == 0) continue;
            double ms = LatencyHistogram.ValueOf(i) / 1000.0;
            int b = 0;
            while (b < labels.Length - 1 && ms >= edges[b + 1]) b++;
            bins[b] += raw[i];
        }
        double totalLat = Math.Max(1, h.Count);
        sb.Append("<div class=\"card\">").Append(Bars(L.T("Distribuzione delle latenze (ms) – % delle risposte"), labels,
            bins.Select(b => 100.0 * b / totalLat).ToList(), "#2563eb", "%")).Append("</div>");

        // Tabelle
        sb.Append("<div class=\"grid2\"><div><h2>").Append(Esc(L.T("Esiti"))).Append("</h2><table><tr><th>").Append(Esc(L.T("Esito")))
          .Append("</th><th class=\"r\">").Append(Esc(L.T("Query"))).Append("</th><th class=\"r\">%</th></tr>");
        foreach (var (k, v) in s.Outcomes.OrderByDescending(kv => kv.Value))
            sb.Append($"<tr><td>{Esc(k)}</td><td class=\"r\">{N(v)}</td><td class=\"r\">{P(s.Completed == 0 ? double.NaN : 100.0 * v / s.Completed, 3)}</td></tr>");
        sb.Append("</table></div><div><h2>").Append(Esc(L.T("Latenze complessive"))).Append("</h2><table>");
        foreach (var (lbl, val) in new[] { (L.T("Minima"), h.MinMs), (L.T("Media"), h.AvgMs), ("p50", h.PercentileMs(50)), ("p90", h.PercentileMs(90)),
                     ("p95", h.PercentileMs(95)), ("p99", h.PercentileMs(99)), ("p" + 99.9.ToString("0.0", C), h.PercentileMs(99.9)), (L.T("Massima"), h.MaxMs) })
            sb.Append($"<tr><td>{Esc(lbl)}</td><td class=\"r\">{Ms(val)}</td></tr>");
        sb.Append("</table></div></div>");

        sb.Append("<div class=\"grid2\"><div><h2>").Append(Esc(L.T("Disponibilità (valutazione per secondo)"))).Append("</h2><table><tr><th>")
          .Append(Esc(L.T("Stato"))).Append("</th><th class=\"r\">").Append(Esc(L.T("Durata"))).Append("</th><th class=\"r\">%</th></tr>");
        foreach (var lbl in new[] { "OK", "DEGRADATO", "NON DISPONIBILE", "NESSUN DATO" })
        {
            long v = s.HealthSeconds.GetValueOrDefault(lbl);
            sb.Append($"<tr><td>{Esc(IntervalStats.HealthDisplay(lbl))}</td><td class=\"r\">{StatsCollector.FormatDuration(TimeSpan.FromSeconds(v))}</td><td class=\"r\">{P(healthTotal == 0 ? double.NaN : 100.0 * v / healthTotal, 2)}</td></tr>");
        }
        sb.Append("</table><p class=\"note\">").Append(Esc(L.F("Finestra mobile {0} s. Degradato: successo sotto {1}% oppure p95 oltre {2} ms. Non disponibile: successo sotto {3}% o nessuna risposta.",
            cfg.HealthWindowSeconds, cfg.DegradedSuccessPct, cfg.DegradedP95Ms, cfg.DownSuccessPct))).Append("</p></div>");
        sb.Append("<div><h2>").Append(Esc(L.T("Altri indicatori"))).Append("</h2><table>");
        void Ind(string label, string value) => sb.Append($"<tr><td>{Esc(label)}</td><td class=\"r\">{Esc(value)}</td></tr>");
        Ind(L.T("Risposte con flag AA (autoritative)"), P(s.Responses == 0 ? double.NaN : 100.0 * s.Authoritative / s.Responses, 2));
        Ind(L.T("Risposte troncate (TC)"), N(s.Truncated));
        Ind(L.T("Risposte tardive/scartate"), N(s.StrayResponses));
        Ind(L.T("Connessioni aperte (TCP/TLS/QUIC)"), N(s.ConnectionsOpened));
        Ind(L.T("Byte ricevuti"), N(s.BytesIn));
        Ind(L.T("Righe nel log per query"), L.F("{0} in {1} file", N(s.QueryLogLines), s.QueryLogFiles));
        sb.Append("</table></div></div>");

        if (s.ErrorDetails.Count > 0)
        {
            sb.Append("<h2>").Append(Esc(L.T("Dettaglio errori (principali)"))).Append("</h2><table><tr><th>").Append(Esc(L.T("Errore")))
              .Append("</th><th class=\"r\">").Append(Esc(L.T("Occorrenze"))).Append("</th></tr>");
            foreach (var (k, v) in s.ErrorDetails.OrderByDescending(kv => kv.Value).Take(25))
                sb.Append($"<tr><td>{Esc(k)}</td><td class=\"r\">{N(v)}</td></tr>");
            sb.Append("</table>");
        }

        if (s.Templates.Count > 0)
        {
            sb.Append("<h2>").Append(Esc(L.T("Risultati per query"))).Append("</h2><table><tr>");
            foreach (var (head, right) in new[] { (L.T("Query"), false), (L.T("Completate"), true), (L.T("Successo"), true), (L.T("Timeout"), true),
                         (L.T("Errori"), true), (L.T("Latenza media"), true), ("AA", true) })
                sb.Append(right ? "<th class=\"r\">" : "<th>").Append(Esc(head)).Append("</th>");
            sb.Append("</tr>");
            foreach (var t in s.Templates.OrderByDescending(t => t.Completed).Take(200))
                sb.Append($"<tr><td class=\"mono\">{Esc(t.Query)}</td><td class=\"r\">{N(t.Completed)}</td><td class=\"r\">{P(t.SuccessPct, 2)}</td><td class=\"r\">{N(t.Timeouts)}</td><td class=\"r\">{N(t.Errors)}</td><td class=\"r\">{Ms(t.AvgMs)}</td><td class=\"r\">{P(t.AaPct, 1)}</td></tr>");
            sb.Append("</table>");
        }

        var important = events.Where(e => e.Level != "INFO" || e.Category is "Sessione" or "Salute" or "Report").ToList();
        sb.Append("<h2>").Append(Esc(L.F("Eventi ({0})", important.Count))).Append("</h2><table><tr><th>").Append(Esc(L.T("Ora"))).Append("</th><th>")
          .Append(Esc(L.T("Livello"))).Append("</th><th>").Append(Esc(L.T("Categoria"))).Append("</th><th>").Append(Esc(L.T("Messaggio"))).Append("</th></tr>");
        foreach (var e in important.Take(1000))
            sb.Append($"<tr class=\"lv-{e.Level.ToLowerInvariant()}\"><td class=\"nw\">{e.TimeLocal:dd/MM HH:mm:ss}</td><td>{Esc(e.Level)}</td><td>{Esc(Labels.EventCategory(e.Category))}</td><td>{Esc(e.Message)}</td></tr>");
        if (important.Count > 1000) sb.Append("<tr><td colspan=\"4\">").Append(Esc(L.F("… altri {0} eventi in events.csv", important.Count - 1000))).Append("</td></tr>");
        sb.Append("</table>");

        // Configurazione
        sb.Append("<h2>").Append(Esc(L.T("Configurazione del test"))).Append("</h2><table class=\"cfg\">");
        void Row(string k, string v) => sb.Append($"<tr><td>{Esc(k)}</td><td>{Esc(v)}</td></tr>");
        Row(L.T("Destinazione"), cfg.TargetDescription);
        Row(L.T("Indirizzo risolto"), info.ResolvedTarget);
        if (cfg.Protocol is DnsProtocol.DoT or DnsProtocol.DoQ) Row(L.T("Nome TLS (SNI)"), string.IsNullOrWhiteSpace(cfg.TlsServerName) ? cfg.Server : cfg.TlsServerName);
        if (cfg.Protocol is DnsProtocol.DoT or DnsProtocol.DoQ or DnsProtocol.DoH) Row(L.T("Verifica certificato"), cfg.TlsSkipValidation ? L.T("DISATTIVATA") : L.T("attiva"));
        if (cfg.Protocol != DnsProtocol.Udp) Row(L.T("Riuso connessioni"), cfg.ReuseConnections ? L.T("sì") : L.T("no (nuova connessione per query)"));
        Row(L.T("Worker"), cfg.Concurrency.ToString());
        Row(L.T("QPS obiettivo"), cfg.TargetQps <= 0 ? L.T("illimitato (massimo throughput)")
            : cfg.RampUpSeconds > 0 ? L.F("{0:N0} (rampa {1} s)", cfg.TargetQps, cfg.RampUpSeconds) : cfg.TargetQps.ToString("N0", C));
        Row(L.T("Durata pianificata"), cfg.DurationSeconds > 0 ? StatsCollector.FormatDuration(TimeSpan.FromSeconds(cfg.DurationSeconds)) : L.T("fino ad arresto manuale"));
        Row(L.T("Timeout per query"), cfg.TimeoutMs + " ms");
        Row(L.T("Flag RD"), cfg.RecursionDesired ? L.T("1 (ricorsione richiesta)") : L.T("0 (come un resolver verso un autoritativo)"));
        Row("EDNS0", !cfg.UseEdns ? L.T("no") : cfg.DnssecOk ? L.F("sì, buffer {0} byte, DO=1", cfg.EdnsBufferSize) : L.F("sì, buffer {0} byte", cfg.EdnsBufferSize));
        Row(L.T("Ordine query"), cfg.Order == QueryOrder.Random ? L.T("casuale") : L.T("in sequenza (round-robin)"));
        Row(L.T("Elenco query"), L.F("{0} voci", cfg.Queries.Count(q => !string.IsNullOrWhiteSpace(q) && !q.TrimStart().StartsWith('#'))));
        Row(L.T("Log per query"), cfg.QueryLog switch { QueryLogMode.All => L.T("tutte le query"), QueryLogMode.FailuresOnly => L.T("solo fallimenti"), _ => L.T("disattivato") });
        Row(L.T("Postazione di test"), $"{info.Machine} – {info.OsDescription}");
        Row(L.T("Versione software"), info.AppVersion);
        sb.Append("</table>");

        sb.Append("<h2>").Append(Esc(L.T("File della sessione"))).Append("</h2><ul class=\"files\">");
        void FileLi(string name, string what) => sb.Append($"<li><b>{name}</b> – {Esc(what)}</li>");
        FileLi("queries_NNNN.csv", L.T("una riga per query (orario, nome, tipo, esito, RCODE, latenza, flag AA/TC, dettaglio dell'errore)"));
        FileLi("intervals_1s.csv / intervals_1m.csv", L.T("aggregati al secondo e al minuto (QPS, esiti, percentili di latenza, stato del server)"));
        FileLi("events.csv / app.log", L.T("eventi della sessione e cambi di stato del server"));
        FileLi("session.json / summary.json", L.T("configurazione e totali, da cui questo report si può rigenerare"));
        sb.Append("</ul>");

        sb.Append("<footer>").Append(Esc(L.F("Generato il {0:dd/MM/yyyy HH:mm:ss} da DNS Load Tester v{1}. Latenze misurate lato client, dall'invio alla ricezione completa della risposta; percentili calcolati su istogramma log-lineare (errore ≤ 3%).",
            DateTime.Now, info.AppVersion))).Append("</footer>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static string TargetShort(TestConfig c) =>
        c.Protocol == DnsProtocol.DoH ? (Uri.TryCreate(c.DohUrl, UriKind.Absolute, out var u) ? u.Host : c.DohUrl) : c.Server;

    private static void Kpi(StringBuilder sb, string label, string value, string? sub = null)
    {
        sb.Append($"<div class=\"kpi\"><div class=\"kl\">{Esc(label)}</div><div class=\"kv\">{Esc(value)}</div>");
        if (sub != null) sb.Append($"<div class=\"ks\">{Esc(sub)}</div>");
        sb.Append("</div>");
    }

    private const string Css = """
        :root{--fg:#111827;--muted:#6b7280;--line:#e5e7eb;--bg:#ffffff;--card:#f9fafb;--accent:#2563eb}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.45 "Segoe UI",system-ui,-apple-system,sans-serif}
        main{max-width:1100px;margin:0 auto;padding:28px 24px 40px}
        .brand{font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:var(--accent);font-weight:600}
        h1{font-size:24px;margin:6px 0 4px}h2{font-size:17px;margin:28px 0 10px;border-bottom:1px solid var(--line);padding-bottom:6px}
        .sub,.muted{color:var(--muted)}.muted{font-weight:400;font-size:13px}
        .verdict{margin:18px 0;padding:12px 16px;border-radius:8px;border-left:5px solid}
        .verdict.good{background:#f0fdf4;border-color:#16a34a}.verdict.warn{background:#fffbeb;border-color:#f59e0b}
        .verdict.bad{background:#fef2f2;border-color:#dc2626}.verdict.na{background:var(--card);border-color:#9ca3af}
        .kpis{display:grid;grid-template-columns:repeat(4,1fr);gap:10px}
        .kpi{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:10px 12px}
        .kl{font-size:12px;color:var(--muted)}.kv{font-size:20px;font-weight:600;font-variant-numeric:tabular-nums}.ks{font-size:12px;color:var(--muted)}
        .card{border:1px solid var(--line);border-radius:8px;padding:6px 8px;margin:10px 0;break-inside:avoid}
        svg.chart{width:100%;height:auto;display:block}
        svg .ct{font-size:14px;font-weight:600;fill:#111827}svg .ax{font-size:11px;fill:#6b7280}svg .lg{font-size:12px;fill:#374151}
        svg .grid{stroke:#e5e7eb}svg .gridv{stroke:#f3f4f6}svg .frame{fill:none;stroke:#d1d5db}svg .na{font-size:14px;fill:#9ca3af}
        table{width:100%;border-collapse:collapse;font-size:13px;margin:6px 0}
        th,td{padding:5px 8px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}th{background:var(--card);font-weight:600}
        td.r,th.r{text-align:right;font-variant-numeric:tabular-nums}.mono{font-family:Consolas,monospace}.nw{white-space:nowrap}
        table.cfg td:first-child{width:32%;color:var(--muted)}
        tr.lv-warn td{background:#fffbeb}tr.lv-error td{background:#fef2f2}
        .grid2{display:grid;grid-template-columns:1fr 1fr;gap:24px}
        .note{font-size:12px;color:var(--muted)}ul.files{padding-left:18px}footer{margin-top:30px;font-size:12px;color:var(--muted)}
        @media (max-width:800px){.kpis{grid-template-columns:repeat(2,1fr)}.grid2{grid-template-columns:1fr}}
        @media print{main{max-width:none;padding:0}h2{break-after:avoid}table{break-inside:auto}tr{break-inside:avoid}}
        """;
}
