using System.IO;
using System.Windows.Media;
using DnsLoadTester.Core;
using DnsLoadTester.Core.Output;
using DnsLoadTester.Core.Stats;

namespace DnsLoadTester.App.Controls;

/// <summary>
/// Serie temporale a passo fisso (1 s o 60 s): il punto i ha ascissa i × Step secondi dall'avvio.
/// Valori in float (4 byte): 8 serie al secondo occupano ~32 byte/s, cioè ~20 MB per 7 giorni di test.
/// NaN = assenza di dato (interrompe la linea).
/// </summary>
public sealed class ChartSeries(string name, Color color, double step, bool dashed = false)
{
    private readonly List<float> _v = [];

    public string Name { get; } = name;
    public double Step { get; } = step;
    public Pen Pen { get; } = MakePen(color, dashed);
    public Brush Brush { get; } = MakeBrush(color);
    public int Count => _v.Count;
    public float this[int i] => _v[i];

    /// <summary>Imposta il valore all'indice indicato, riempiendo di NaN gli eventuali buchi.</summary>
    public void Set(long index, double value)
    {
        if (index < 0 || index - _v.Count > 50_000_000) return;    // protezione da indici anomali
        while (_v.Count < index) _v.Add(float.NaN);
        if (index == _v.Count) _v.Add((float)value);
        else _v[(int)index] = (float)value;
    }

    public void Clear() => _v.Clear();

    private static Pen MakePen(Color c, bool dashed)
    {
        var p = new Pen(new SolidColorBrush(c), 1.6) { LineJoin = PenLineJoin.Round };
        if (dashed) p.DashStyle = new DashStyle([4, 3], 0);
        p.Freeze();
        return p;
    }

    private static Brush MakeBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

/// <summary>Tutti i dati dei tre grafici, a risoluzione 1 secondo e 1 minuto, per l'intera sessione.</summary>
public sealed class ChartData
{
    public ChartSeries[] SecQps { get; }
    public ChartSeries[] SecLat { get; }
    public ChartSeries[] SecFail { get; }
    public ChartSeries[] MinQps { get; }
    public ChartSeries[] MinLat { get; }
    public ChartSeries[] MinFail { get; }

    /// <summary>Istante a cui corrisponde l'ascissa 0.</summary>
    public DateTime StartLocal { get; set; } = DateTime.Now;

    /// <summary>Fine dei dati disponibili, in secondi dall'avvio.</summary>
    public double EndSeconds { get; private set; }

    public bool HasData => EndSeconds > 0;

    public ChartData()
    {
        (SecQps, SecLat, SecFail) = Make(1);
        (MinQps, MinLat, MinFail) = Make(60);
    }

    private static (ChartSeries[], ChartSeries[], ChartSeries[]) Make(double step) =>
    (
        [new(L.T("Inviate/s"), Color.FromRgb(0x94, 0xA3, 0xB8), step, dashed: true), new(L.T("Risposte/s"), Color.FromRgb(0x25, 0x63, 0xEB), step), new(L.T("Successi/s"), Color.FromRgb(0x16, 0xA3, 0x4A), step)],
        [new("p50", Color.FromRgb(0x16, 0xA3, 0x4A), step), new("p95", Color.FromRgb(0xF5, 0x9E, 0x0B), step), new("p99", Color.FromRgb(0xDC, 0x26, 0x26), step)],
        [new(L.T("% fallite"), Color.FromRgb(0xDC, 0x26, 0x26), step), new(L.T("% timeout"), Color.FromRgb(0x7C, 0x3A, 0xED), step)]
    );

    public void AddSecond(IntervalStats s)
    {
        long i = s.Index;
        Put(SecQps, i, s.SentQps, s.ResponseQps, s.Seconds > 0 ? s.Success / s.Seconds : 0);
        Put(SecLat, i, s.LatP50Ms, s.LatP95Ms, s.LatP99Ms);
        Put(SecFail, i, s.FailurePct, s.TimeoutPct);
        EndSeconds = Math.Max(EndSeconds, i + Math.Max(s.Seconds, 1));
    }

    public void AddMinute(IntervalStats m)
    {
        long i = m.Index;
        Put(MinQps, i, m.SentQps, m.ResponseQps, m.Seconds > 0 ? m.Success / m.Seconds : 0);
        Put(MinLat, i, m.LatP50Ms, m.LatP95Ms, m.LatP99Ms);
        Put(MinFail, i, m.FailurePct, m.TimeoutPct);
        EndSeconds = Math.Max(EndSeconds, i * 60 + Math.Max(m.Seconds, 1));
    }

    private static void Put(ChartSeries[] set, long index, params double[] values)
    {
        for (int k = 0; k < set.Length; k++) set[k].Set(index, values[k]);
    }

    /// <summary>Carica i grafici di una sessione salvata leggendo intervals_1s.csv e intervals_1m.csv (a flusso).</summary>
    public static ChartData LoadFromSession(string folder, DateTime startLocal, CsvFormat csv)
    {
        var d = new ChartData { StartLocal = startLocal };
        IntervalWriter.ReadEach(Path.Combine(folder, SessionFiles.Intervals1s), csv, d.AddSecond, 1);
        IntervalWriter.ReadEach(Path.Combine(folder, SessionFiles.Intervals1m), csv, d.AddMinute, 60);
        return d;
    }
}
