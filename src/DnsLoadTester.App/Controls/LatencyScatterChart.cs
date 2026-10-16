using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DnsLoadTester.Core;
using DnsLoadTester.Core.Analysis;

namespace DnsLoadTester.App.Controls;

/// <summary>
/// Grafico a dispersione: un punto per query (asse X = istante di conclusione, asse Y = latenza), colore per esito.
/// I punti sono rasterizzati in una bitmap per pixel, quindi milioni di query si disegnano in poche decine di ms;
/// nei pixel con più query prevale l'esito più grave (errore di trasporto, timeout, RCODE di errore, risposta valida),
/// così un singolo problema non resta nascosto sotto migliaia di risposte corrette.
/// </summary>
public sealed class LatencyScatterChart : FrameworkElement
{
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface FaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush AxisBrush = Frozen(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush TitleBrush = Frozen(Color.FromRgb(0x11, 0x18, 0x27));
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(0xEE, 0xF0, 0xF3), 1);
    private static readonly Pen FramePen = FrozenPen(Color.FromRgb(0xD1, 0xD5, 0xDB), 1);
    private static readonly Pen CursorPen = FrozenPen(Color.FromArgb(0x90, 0x37, 0x41, 0x51), 1);
    private static readonly Brush TipBg = Frozen(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));

    // Colori per gruppo di esito, in ordine di priorità crescente
    private static readonly Color[] GroupColors =
    [
        Color.FromRgb(0x25, 0x63, 0xEB),   // 0 risposta valida (NOERROR / NXDOMAIN)
        Color.FromRgb(0xEA, 0x8A, 0x0C),   // 1 RCODE di errore (SERVFAIL, REFUSED, …)
        Color.FromRgb(0x7C, 0x3A, 0xED),   // 2 timeout
        Color.FromRgb(0xDC, 0x26, 0x26),   // 3 errore di trasporto
    ];

    private const double L0 = 58, R = 14, T0 = 26, B = 24;

    public QueryLog? Log { get; set; }
    public int[] Rows { get; set; } = [];
    public bool LogScale { get; set; } = true;
    public string Title { get; set; } = "";
    public double ViewStart { get; set; }
    public double ViewEnd { get; set; } = 60;

    public event Action<double, double>? ViewRequested;
    public event Action? ResetRequested;

    private WriteableBitmap? _bmp;
    private (double, double, int, int, int[], bool, double) _bmpKey;
    private double _yMax = 1, _yMin = 0.1;
    private int _visible;
    private Point? _mouse;
    private Point _dragOrigin;
    private double _dragStart, _dragEnd;
    private bool _dragging;

    public LatencyScatterChart()
    {
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    public void Refresh() => InvalidateVisual();

    private Rect Plot => new(L0, T0, Math.Max(10, ActualWidth - L0 - R), Math.Max(10, ActualHeight - T0 - B));

    // ------------------------------------------------------------------ interazione (come TimeSeriesChart)

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_dragging)
        {
            double secPerPx = (_dragEnd - _dragStart) / Plot.Width;
            double shift = -(p.X - _dragOrigin.X) * secPerPx;
            ViewRequested?.Invoke(_dragStart + shift, _dragEnd + shift);
        }
        _mouse = p;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _mouse = null; InvalidateVisual(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2) { ResetRequested?.Invoke(); e.Handled = true; return; }
        _dragOrigin = e.GetPosition(this);
        _dragStart = ViewStart; _dragEnd = ViewEnd;
        _dragging = CaptureMouse();
        Cursor = Cursors.SizeWE;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); EndDrag(); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); EndDrag(); }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        Cursor = Cursors.Cross;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        double width = ViewEnd - ViewStart, notches = e.Delta / 120.0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            double frac = Math.Clamp((e.GetPosition(this).X - Plot.Left) / Plot.Width, 0, 1);
            double anchor = ViewStart + frac * width, nw = width * Math.Pow(0.8, notches);
            ViewRequested?.Invoke(anchor - frac * nw, anchor + (1 - frac) * nw);
        }
        else
        {
            double shift = -notches * width * 0.1;
            ViewRequested?.Invoke(ViewStart + shift, ViewEnd + shift);
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ disegno

    private static int GroupOf(QueryClass c) => c switch
    {
        QueryClass.NoError or QueryClass.NxDomain => 0,
        QueryClass.Timeout => 2,
        QueryClass.Error => 3,
        _ => 1,
    };

    private double YToPx(double ms, Rect plot)
    {
        if (LogScale)
        {
            double v = Math.Log10(Math.Max(ms, _yMin)), lo = Math.Log10(_yMin), hi = Math.Log10(_yMax);
            return plot.Bottom - (v - lo) / (hi - lo) * plot.Height;
        }
        return plot.Bottom - Math.Clamp(ms, 0, _yMax) / _yMax * plot.Height;
    }

    private double PxToY(double py, Rect plot)
    {
        double f = (plot.Bottom - py) / plot.Height;
        if (LogScale) return Math.Pow(10, Math.Log10(_yMin) + f * (Math.Log10(_yMax) - Math.Log10(_yMin)));
        return f * _yMax;
    }

    /// <summary>Rasterizza i punti visibili. Ricalcolata solo se cambiano dati, vista, dimensioni o scala.</summary>
    private void BuildBitmap(Rect plot)
    {
        int w = (int)Math.Max(1, plot.Width), h = (int)Math.Max(1, plot.Height);
        var key = (ViewStart, ViewEnd, w, h, Rows, LogScale, 0d);
        if (_bmp != null && _bmpKey.Equals(key)) return;
        _bmpKey = key;
        var log = Log;
        _visible = 0;
        if (log == null || Rows.Length == 0) { _bmp = null; return; }

        double x0 = ViewStart, span = Math.Max(1e-6, ViewEnd - ViewStart);
        int a = QueryLogAnalyzer.LowerBound(log, Rows, x0), b = QueryLogAnalyzer.LowerBound(log, Rows, ViewEnd);
        _visible = b - a;

        // Scala Y: massimo delle latenze visibili (minimo 1 ms), minimo 0,1 ms in scala logaritmica
        float max = 1;
        for (int k = a; k < b; k++) { float v = log.LatMs[Rows[k]]; if (v > max) max = v; }
        _yMax = NiceCeil(max * 1.1);
        _yMin = 0.1;

        var count = new int[w * h];
        var group = new byte[w * h];
        int size = _visible < 20_000 ? 3 : _visible < 200_000 ? 2 : 1;   // punti più grandi quando sono pochi
        var plotLocal = new Rect(0, 0, w, h);
        for (int k = a; k < b; k++)
        {
            int i = Rows[k];
            // limitati all'area: una latenza in fondo alla scala (errori immediati, < 0,1 ms) darebbe py == h
            int px = Math.Clamp((int)((log.T[i] - x0) / span * (w - 1)), 0, w - 1);
            int py = Math.Clamp((int)YToPx(log.LatMs[i], plotLocal), 0, h - 1);
            int g = GroupOf(log.Class[i]);
            for (int dy = 0; dy < size; dy++)
                for (int dx = 0; dx < size; dx++)
                {
                    int x = px + dx - size / 2, y = py + dy - size / 2;
                    if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
                    int ix = y * w + x;
                    count[ix]++;
                    if (g > group[ix]) group[ix] = (byte)g;
                }
        }
        var pixels = new uint[w * h];
        for (int ix = 0; ix < pixels.Length; ix++)
        {
            int c = count[ix];
            if (c == 0) continue;
            var col = GroupColors[group[ix]];
            uint alpha = (uint)Math.Min(255, 110 + 35 * Math.Log2(c));
            // BGRA premoltiplicato
            pixels[ix] = (alpha << 24) | ((uint)(col.R * alpha / 255) << 16) | ((uint)(col.G * alpha / 255) << 8) | (uint)(col.B * alpha / 255);
        }
        _bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
        _bmp.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        _bmp.Freeze();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 80 || h < 60) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var plot = Plot;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));

        var title = Text(Title, 13, TitleBrush, dpi, true);
        dc.DrawText(title, new Point(L0, 4));
        double lx = L0 + title.Width + 18;
        string[] names = [L.T("risposta valida"), L.T("RCODE di errore"), L.T("timeout"), L.T("errore di trasporto")];
        for (int g = 0; g < 4; g++)
        {
            dc.DrawEllipse(Frozen(GroupColors[g]), null, new Point(lx + 5, 12), 4, 4);
            var ft = Text(names[g], 11, AxisBrush, dpi);
            dc.DrawText(ft, new Point(lx + 13, 5));
            lx += 24 + ft.Width;
        }

        BuildBitmap(plot);
        var stats = Text(L.F("{0:N0} query visibili", _visible), 11, AxisBrush, dpi);
        dc.DrawText(stats, new Point(w - R - stats.Width, 5));

        // Griglia Y
        var rect = new Rect(0, 0, plot.Width, plot.Height);
        IEnumerable<double> ticks = LogScale
            ? Enumerable.Range(-1, 7).Select(e => Math.Pow(10, e)).Where(v => v >= _yMin && v <= _yMax)
            : Enumerable.Range(0, 5).Select(i => _yMax * i / 4);
        foreach (var v in ticks)
        {
            double y = Math.Round(plot.Top + YToPx(v, rect)) + 0.5;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var ft = Text(Fmt(v), 10.5, AxisBrush, dpi);
            dc.DrawText(ft, new Point(plot.Left - 6 - ft.Width, y - ft.Height / 2));
        }
        var unit = Text(LogScale ? L.T("ms (scala log)") : "ms", 10.5, AxisBrush, dpi);
        dc.PushTransform(new RotateTransform(-90, 10, plot.Top + plot.Height / 2));
        dc.DrawText(unit, new Point(10 - unit.Width / 2, plot.Top + plot.Height / 2 - unit.Height / 2 - 2));
        dc.Pop();

        // Griglia X
        double x0 = ViewStart, x1 = Math.Max(ViewEnd, ViewStart + 1), span = x1 - x0;
        var start = Log?.StartLocal ?? DateTime.Now;
        double step = NiceTimeStep(span / Math.Max(2, plot.Width / 110));
        bool multiDay = start.AddSeconds(x0).Date != start.AddSeconds(x1).Date;
        string fmt = multiDay ? (step >= 60 ? "dd/MM HH:mm" : "dd/MM HH:mm:ss") : step >= 60 ? "HH:mm" : step >= 1 ? "HH:mm:ss" : "HH:mm:ss.f";
        for (double t = Math.Ceiling(x0 / step) * step; t <= x1; t += step)
        {
            double x = Math.Round(plot.Left + (t - x0) / span * plot.Width) + 0.5;
            dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var ft = Text(start.AddSeconds(t).ToString(fmt, CultureInfo.InvariantCulture), 10.5, AxisBrush, dpi);
            if (x - ft.Width / 2 > plot.Left - 20 && x + ft.Width / 2 < w) dc.DrawText(ft, new Point(x - ft.Width / 2, plot.Bottom + 4));
        }

        if (_bmp != null) dc.DrawImage(_bmp, plot);
        else if (Log != null)
        {
            var ft = Text(L.T("Nessuna query con i filtri impostati"), 13, AxisBrush, dpi);
            dc.DrawText(ft, new Point(plot.Left + (plot.Width - ft.Width) / 2, plot.Top + (plot.Height - ft.Height) / 2));
        }
        dc.DrawRectangle(null, FramePen, plot);

        if (_mouse is { } m && plot.Contains(m) && !_dragging && Log != null)
        {
            dc.DrawLine(CursorPen, new Point(m.X, plot.Top), new Point(m.X, plot.Bottom));
            dc.DrawLine(CursorPen, new Point(plot.Left, m.Y), new Point(plot.Right, m.Y));
            double tv = x0 + (m.X - plot.Left) / plot.Width * span;
            double yv = PxToY(m.Y - plot.Top, rect);
            var ft = Text($"{start.AddSeconds(tv):dd/MM HH:mm:ss.fff}  ·  {Fmt(yv)} ms", 11, TitleBrush, dpi);
            double tx = m.X + 12 + ft.Width + 12 > w ? m.X - 24 - ft.Width : m.X + 12;
            dc.DrawRoundedRectangle(TipBg, FramePen, new Rect(tx, plot.Top + 4, ft.Width + 12, ft.Height + 6), 4, 4);
            dc.DrawText(ft, new Point(tx + 6, plot.Top + 7));
        }
    }

    private static double NiceCeil(double v)
    {
        if (v <= 0) return 1;
        double e = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double f = v / e;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * e;
    }

    private static double NiceTimeStep(double approx)
    {
        double[] steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400, 172800, 604800];
        foreach (var s in steps) if (s >= approx) return s;
        return steps[^1];
    }

    private static string Fmt(double v) =>
        v >= 10000 ? (v / 1000).ToString("0.#", L.Culture) + "k" :
        v >= 100 ? v.ToString("0", L.Culture) :
        v >= 10 ? v.ToString("0.#", L.Culture) :
        v >= 1 ? v.ToString("0.##", L.Culture) : v.ToString("0.###", L.Culture);

    private static FormattedText Text(string s, double size, Brush b, double dpi, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, b, dpi);

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double t) { var p = new Pen(new SolidColorBrush(c), t); p.Freeze(); return p; }
}
