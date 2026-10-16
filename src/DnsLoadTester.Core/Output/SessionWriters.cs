using System.Text;
using DnsLoadTester.Core.Dns;
using DnsLoadTester.Core.Stats;

namespace DnsLoadTester.Core.Output;

/// <summary>Log per singola query con rotazione per dimensione. Chiamato solo dal thread consumatore.</summary>
public sealed class QueryLogWriter : IDisposable
{
    private readonly string _dir;
    private readonly CsvFormat _csv;
    private readonly long _rotateBytes;
    private StreamWriter? _w;
    private long _bytes;
    public int FileCount { get; private set; }
    public long Lines { get; private set; }
    private readonly StringBuilder _sb = new(256);

    public static readonly string[] Header =
    [
        "timestamp", "elapsed_ms", "seq", "worker", "qname", "qtype", "outcome", "rcode",
        "latency_ms", "resp_bytes", "answers", "aa", "tc", "detail",
    ];

    public QueryLogWriter(string dir, CsvFormat csv, int rotateMb)
    {
        _dir = dir; _csv = csv;
        _rotateBytes = Math.Max(1, rotateMb) * 1024L * 1024L;
    }

    private void Open()
    {
        FileCount++;
        var path = Path.Combine(_dir, $"{SessionFiles.QueryLogPrefix}{FileCount:D4}.csv");
        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        _w = new StreamWriter(fs, SessionFiles.Utf8Bom, 1 << 16);
        var header = _csv.Join(Header);
        _w.WriteLine(header);
        _bytes = header.Length + 2;
    }

    public void Write(in QueryResult r, DateTime startUtc)
    {
        if (_w == null || _bytes >= _rotateBytes)
        {
            _w?.Dispose();
            Open();
        }
        var sb = _sb.Clear();
        char s = _csv.Sep;
        sb.Append(r.CompletedUtc.ToLocalTime().ToString(CsvFormat.TimestampFormat)).Append(s)
          .Append(((long)(r.CompletedUtc - startUtc).TotalMilliseconds).ToString()).Append(s)
          .Append(r.Seq).Append(s)
          .Append(r.WorkerId).Append(s)
          .Append(_csv.Escape(r.QName)).Append(s)
          .Append(DnsTypes.ToName(r.QType)).Append(s)
          .Append(r.OutcomeLabel).Append(s)
          .Append(r.IsResponse ? r.Rcode.ToString() : "").Append(s)
          .Append(_csv.Num(r.LatencyUs / 1000.0)).Append(s)
          .Append(r.IsResponse ? r.ResponseBytes.ToString() : "").Append(s)
          .Append(r.IsResponse ? r.Answers.ToString() : "").Append(s)
          .Append(r.IsResponse ? (r.Authoritative ? "1" : "0") : "").Append(s)
          .Append(r.IsResponse ? (r.Truncated ? "1" : "0") : "").Append(s)
          .Append(_csv.Escape(r.Detail));
        _w!.WriteLine(sb);
        _bytes += sb.Length + 2;
        Lines++;
    }

    public void Flush() => _w?.Flush();

    public void Dispose() { _w?.Flush(); _w?.Dispose(); _w = null; }
}

/// <summary>Scrive gli aggregati per intervallo (1 s o 1 min) con flush immediato.</summary>
public sealed class IntervalWriter : IDisposable
{
    private readonly StreamWriter _w;
    private readonly CsvFormat _csv;

    public static readonly string[] Header =
    [
        "timestamp", "elapsed_s", "duration_s", "sent", "completed", "responses", "success", "success_pct",
        "noerror", "nxdomain", "servfail", "refused", "other_rcode", "timeouts", "net_errors", "other_errors",
        "truncated", "bytes_in", "qps_sent", "qps_responses",
        "lat_min_ms", "lat_avg_ms", "lat_p50_ms", "lat_p90_ms", "lat_p95_ms", "lat_p99_ms", "lat_max_ms", "health",
    ];

    public IntervalWriter(string path, CsvFormat csv)
    {
        _csv = csv;
        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 14);
        _w = new StreamWriter(fs, SessionFiles.Utf8Bom);
        _w.WriteLine(csv.Join(Header));
        _w.Flush();
    }

    public void Write(IntervalStats st, double elapsedSeconds)
    {
        var c = _csv;
        _w.WriteLine(c.Join(
        [
            st.StartLocal.ToString(CsvFormat.TimestampFormat), c.Num(elapsedSeconds, 0), c.Num(st.Seconds, 0),
            st.Sent.ToString(), st.Completed.ToString(), st.Responses.ToString(), st.Success.ToString(), c.Num(st.SuccessPct, 3),
            st.NoError.ToString(), st.NxDomain.ToString(), st.ServFail.ToString(), st.Refused.ToString(), st.OtherRcode.ToString(),
            st.Timeouts.ToString(), st.NetErrors.ToString(), st.OtherErrors.ToString(), st.Truncated.ToString(), st.BytesIn.ToString(),
            c.Num(st.SentQps, 2), c.Num(st.ResponseQps, 2),
            c.Num(st.LatMinMs), c.Num(st.LatAvgMs), c.Num(st.LatP50Ms), c.Num(st.LatP90Ms),
            c.Num(st.LatP95Ms), c.Num(st.LatP99Ms), c.Num(st.LatMaxMs), IntervalStats.HealthLabel(st.Health),
        ]));
        _w.Flush();
    }

    /// <summary>Rilegge un file di intervalli (per il report o per riaprire una sessione).</summary>
    /// <summary>Rilegge un file di intervalli (per il report o per riaprire una sessione).</summary>
    public static List<IntervalStats> Read(string path, CsvFormat c, int secondsPerIndex = 1)
    {
        var list = new List<IntervalStats>();
        ReadEach(path, c, list.Add, secondsPerIndex);
        return list;
    }

    /// <summary>
    /// Legge un file di intervalli riga per riga senza trattenerlo in memoria (sessioni di più giorni).
    /// L'indice di ogni intervallo è ricavato da elapsed_s / <paramref name="secondsPerIndex"/> (1 per i secondi, 60 per i minuti).
    /// Le righe incomplete (crash durante la scrittura) vengono ignorate. Restituisce il numero di righe lette.
    /// </summary>
    public static long ReadEach(string path, CsvFormat c, Action<IntervalStats> onRow, int secondsPerIndex = 1)
    {
        if (!File.Exists(path)) return 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var r = new StreamReader(fs, Encoding.UTF8);
        var header = r.ReadLine();
        if (header == null) return 0;
        var cols = c.Split(header);
        int Ix(string n) => Array.IndexOf(cols, n);
        int iTs = Ix("timestamp"), iEl = Ix("elapsed_s"), iDur = Ix("duration_s"),
            iSent = Ix("sent"), iComp = Ix("completed"), iResp = Ix("responses"), iSucc = Ix("success"),
            iNoErr = Ix("noerror"), iNx = Ix("nxdomain"), iSf = Ix("servfail"), iRef = Ix("refused"), iOth = Ix("other_rcode"),
            iTo = Ix("timeouts"), iNet = Ix("net_errors"), iOe = Ix("other_errors"), iTc = Ix("truncated"), iBytes = Ix("bytes_in"),
            iMin = Ix("lat_min_ms"), iAvg = Ix("lat_avg_ms"), iP50 = Ix("lat_p50_ms"), iP90 = Ix("lat_p90_ms"),
            iP95 = Ix("lat_p95_ms"), iP99 = Ix("lat_p99_ms"), iMax = Ix("lat_max_ms"), iHealth = Ix("health");
        if (new[] { iTs, iEl, iDur, iSent, iComp, iHealth }.Any(i => i < 0)) return 0;
        long index = 0, rows = 0;
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var f = c.Split(line);
            if (f.Length < cols.Length) continue;   // riga parziale (crash durante la scrittura)
            try
            {
                long L(int i) => i < 0 ? 0 : long.Parse(f[i]);
                double D(int i) => i < 0 ? double.NaN : c.ParseNum(f[i]);
                double el = c.ParseNum(f[iEl]);
                long idx = double.IsNaN(el) ? index : (long)Math.Round(el / Math.Max(1, secondsPerIndex));
                index = idx + 1;
                var health = f[iHealth] switch
                {
                    "OK" => HealthState.Ok, "DEGRADATO" => HealthState.Degraded, "NON DISPONIBILE" => HealthState.Down, _ => HealthState.NoData,
                };
                onRow(new IntervalStats
                {
                    Index = idx,
                    StartLocal = DateTime.ParseExact(f[iTs], CsvFormat.TimestampFormat, System.Globalization.CultureInfo.InvariantCulture),
                    Seconds = c.ParseNum(f[iDur]),
                    Sent = L(iSent), Completed = L(iComp), Responses = L(iResp), Success = L(iSucc),
                    NoError = L(iNoErr), NxDomain = L(iNx), ServFail = L(iSf), Refused = L(iRef), OtherRcode = L(iOth),
                    Timeouts = L(iTo), NetErrors = L(iNet), OtherErrors = L(iOe), Truncated = L(iTc), BytesIn = L(iBytes),
                    LatMinMs = D(iMin), LatAvgMs = D(iAvg), LatP50Ms = D(iP50), LatP90Ms = D(iP90),
                    LatP95Ms = D(iP95), LatP99Ms = D(iP99), LatMaxMs = D(iMax),
                    Health = health,
                });
                rows++;
            }
            catch (FormatException) { /* riga incompleta: ignorata */ }
            catch (OverflowException) { }
        }
        return rows;
    }

    public void Dispose() { _w.Flush(); _w.Dispose(); }
}

/// <summary>Evento di sessione (avvio, cambi di stato di salute, errori, stop).</summary>
public sealed record SessionEvent(DateTime TimeLocal, string Level, string Category, string Message);

/// <summary>Log degli eventi (events.csv) e log applicativo (app.log). Thread-safe.</summary>
public sealed class EventLog : IDisposable
{
    private readonly StreamWriter _events;
    private readonly StreamWriter _app;
    private readonly CsvFormat _csv;
    private readonly object _lock = new();
    public event Action<SessionEvent>? EventAdded;

    public EventLog(string dir, CsvFormat csv)
    {
        _csv = csv;
        _events = new StreamWriter(new FileStream(Path.Combine(dir, SessionFiles.EventsCsv), FileMode.Create, FileAccess.Write, FileShare.Read), SessionFiles.Utf8Bom);
        _events.WriteLine(csv.Join(["timestamp", "level", "category", "message"]));
        _events.Flush();
        _app = new StreamWriter(new FileStream(Path.Combine(dir, SessionFiles.AppLog), FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
    }

    public void Event(string level, string category, string message)
    {
        var ev = new SessionEvent(DateTime.Now, level, category, message);
        lock (_lock)
        {
            _events.WriteLine(_csv.Join([ev.TimeLocal.ToString(CsvFormat.TimestampFormat), level, category, _csv.Escape(message)]));
            _events.Flush();
            _app.WriteLine($"{ev.TimeLocal:yyyy-MM-dd HH:mm:ss.fff} [{level}] {category}: {message}");
            _app.Flush();
        }
        EventAdded?.Invoke(ev);
    }

    public void App(string message)
    {
        lock (_lock)
        {
            _app.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
            _app.Flush();
        }
    }

    public static List<SessionEvent> Read(string path, CsvFormat c)
    {
        var list = new List<SessionEvent>();
        if (!File.Exists(path)) return list;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(fs, Encoding.UTF8);
        r.ReadLine();
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            var f = c.Split(line);
            if (f.Length < 4) continue;
            if (DateTime.TryParseExact(f[0], CsvFormat.TimestampFormat, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t))
                list.Add(new SessionEvent(t, f[1], f[2], f[3]));
        }
        return list;
    }

    public void Dispose()
    {
        lock (_lock) { _events.Dispose(); _app.Dispose(); }
    }
}
