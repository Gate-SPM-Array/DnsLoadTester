using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DnsLoadTester.Core;

namespace DnsLoadTester.App.Controls;

/// <summary>
/// Grafico temporale leggero disegnato con DrawingContext, senza dipendenze esterne.
/// Mostra l'intervallo [ViewStart, ViewEnd] (secondi dall'avvio) deciso dal chiamante;
/// per ogni colonna di pixel disegna minimo e massimo, così i picchi restano visibili a qualsiasi scala.
/// Interazioni: rotella = scorre nel tempo, Ctrl+rotella = zoom, trascinamento = scorre, doppio clic = torna al tempo reale.
/// </summary>
public sealed class TimeSeriesChart : FrameworkElement
{
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface FaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush AxisBrush = Frozen(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush TitleBrush = Frozen(Color.FromRgb(0x11, 0x18, 0x27));
    private static readonly Brush PlotBg = Frozen(Colors.White);
    private static readonly Brush NoDataBrush = Frozen(Color.FromRgb(0xF9, 0xFA, 0xFB));
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(0xEE, 0xF0, 0xF3), 1);
    private static readonly Pen ZeroPen = FrozenPen(Color.FromRgb(0xB0, 0xB6, 0xC0), 1);
    private static readonly Pen FramePen = FrozenPen(Color.FromRgb(0xD1, 0xD5, 0xDB), 1);
    private static readonly Pen CursorPen = FrozenPen(Color.FromArgb(0x90, 0x37, 0x41, 0x51), 1);
    private static readonly Brush TipBg = Frozen(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));

    /// <summary>Margine in pixel sotto lo zero: la linea a zero resta visibile sopra il bordo del grafico.</summary>
    private const double ZeroPad = 7;
    private const double L = 58, R = 14, T = 26, B = 24;

    public List<ChartSeries> Series { get; } = [];
    public string Title { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>Se valorizzato, l'asse Y non supera mai questo valore (es. 100 per le percentuali).</summary>
    public double? YCeiling { get; set; }
    /// <summary>Istante corrispondente all'ascissa 0.</summary>
    public DateTime StartLocal { get; set; } = DateTime.Now;
    /// <summary>Inizio e fine dell'intervallo visibile, in secondi dall'avvio.</summary>
    public double ViewStart { get; set; }
    public double ViewEnd { get; set; } = 300;
    /// <summary>Fine dei dati disponibili: l'area oltre è ombreggiata.</summary>
    public double DataEnd { get; set; }

    /// <summary>Richiesta di nuovo intervallo (scorrimento o zoom con mouse). Il chiamante decide e sincronizza gli altri grafici.</summary>
    public event Action<double, double>? ViewRequested;
    /// <summary>Doppio clic: ritorno alla visualizzazione in tempo reale.</summary>
    public event Action? LiveRequested;

    private Point? _mouse;
    private Point _dragOrigin;
    private double _dragStart, _dragEnd;
    private bool _dragging;

    public TimeSeriesChart()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Focusable = false;
        Cursor = Cursors.Cross;
    }

    public void Refresh() => InvalidateVisual();

    private Rect PlotRect => new(L, T, Math.Max(10, ActualWidth - L - R), Math.Max(10, ActualHeight - T - B));

    // ------------------------------------------------------------------ interazione

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_dragging)
        {
            var plot = PlotRect;
            double secPerPx = (_dragEnd - _dragStart) / plot.Width;
            double shift = -(p.X - _dragOrigin.X) * secPerPx;
            ViewRequested?.Invoke(_dragStart + shift, _dragEnd + shift);
        }
        _mouse = p;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2) { LiveRequested?.Invoke(); e.Handled = true; return; }
        _dragOrigin = e.GetPosition(this);
        _dragStart = ViewStart; _dragEnd = ViewEnd;
        _dragging = CaptureMouse();
        Cursor = Cursors.SizeWE;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndDrag();
    }

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
        double width = ViewEnd - ViewStart;
        double notches = e.Delta / 120.0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // Zoom centrato sul punto del mouse
            var plot = PlotRect;
            double frac = Math.Clamp((e.GetPosition(this).X - plot.Left) / plot.Width, 0, 1);
            double anchor = ViewStart + frac * width;
            double factor = Math.Pow(0.8, notches);
            double newWidth = width * factor;
            ViewRequested?.Invoke(anchor - frac * newWidth, anchor + (1 - frac) * newWidth);
        }
        else
        {
            // Rotella in su = indietro nel tempo
            double shift = -notches * width * 0.1;
            ViewRequested?.Invoke(ViewStart + shift, ViewEnd + shift);
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ disegno

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 80 || h < 60) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var plot = PlotRect;
        dc.DrawRectangle(PlotBg, null, new Rect(0, 0, w, h));

        // Titolo e legenda
        var title = Text(Title, 13, TitleBrush, dpi, bold: true);
        dc.DrawText(title, new Point(L, 4));
        double lx = L + title.Width + 18;
        foreach (var s in Series)
        {
            dc.DrawRectangle(s.Brush, null, new Rect(lx, 11, 14, 3));
            var ft = Text(s.Name, 11, AxisBrush, dpi);
            dc.DrawText(ft, new Point(lx + 18, 5));
            lx += 26 + ft.Width;
        }

        double x0 = ViewStart, x1 = ViewEnd;
        if (x1 - x0 < 1) x1 = x0 + 1;
        double span = x1 - x0;
        double Px(double x) => plot.Left + (x - x0) / span * plot.Width;

        // Area senza dati (futuro o oltre la fine della sessione)
        if (DataEnd < x1)
        {
            double nx = Math.Max(plot.Left, Px(DataEnd));
            if (nx < plot.Right) dc.DrawRectangle(NoDataBrush, null, new Rect(nx, plot.Top, plot.Right - nx, plot.Height));
        }

        // Campionamento per colonna di pixel: min e max di ogni serie
        int cols = (int)Math.Max(2, plot.Width);
        var buckets = new List<(ChartSeries S, float[] Min, float[] Max, bool[] Break)>(Series.Count);
        double yMax = 0;
        foreach (var s in Series)
        {
            var mn = new float[cols]; var mx = new float[cols]; var brk = new bool[cols];
            Array.Fill(mn, float.NaN); Array.Fill(mx, float.NaN);
            int i0 = Math.Max(0, (int)Math.Floor(x0 / s.Step) - 1);
            int i1 = Math.Min(s.Count - 1, (int)Math.Ceiling(x1 / s.Step) + 1);
            bool pendingBreak = false;
            for (int i = i0; i <= i1; i++)
            {
                float y = s[i];
                if (float.IsNaN(y) || float.IsInfinity(y)) { pendingBreak = true; continue; }
                int c = (int)Math.Round((i * s.Step - x0) / span * (cols - 1));
                if (c < 0 || c >= cols) continue;
                if (pendingBreak) { brk[c] = true; pendingBreak = false; }
                if (float.IsNaN(mn[c]) || y < mn[c]) mn[c] = y;
                if (float.IsNaN(mx[c]) || y > mx[c]) mx[c] = y;
                if (y > yMax) yMax = y;
            }
            buckets.Add((s, mn, mx, brk));
        }
        yMax = yMax <= 0 ? 1 : NiceCeil(yMax * 1.08);
        if (YCeiling is { } cap && yMax > cap) yMax = cap;
        if (yMax <= 0) yMax = 1;

        double usable = plot.Height - ZeroPad;
        double Py(double y) => plot.Bottom - ZeroPad - Math.Clamp(y, 0, yMax) / yMax * usable;

        // Griglia Y (lo zero ha una linea più marcata, rialzata rispetto al bordo)
        for (int i = 0; i <= 4; i++)
        {
            double v = yMax * i / 4, y = Math.Round(Py(v)) + 0.5;
            dc.DrawLine(i == 0 ? ZeroPen : GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var ft = Text(Fmt(v), 10.5, AxisBrush, dpi);
            dc.DrawText(ft, new Point(plot.Left - 6 - ft.Width, y - ft.Height / 2));
        }
        var unit = Text(Unit, 10.5, AxisBrush, dpi);
        dc.PushTransform(new RotateTransform(-90, 10, plot.Top + plot.Height / 2));
        dc.DrawText(unit, new Point(10 - unit.Width / 2, plot.Top + plot.Height / 2 - unit.Height / 2 - 2));
        dc.Pop();

        // Griglia X con orari reali
        double step = NiceTimeStep(span / Math.Max(2, plot.Width / 110));
        bool multiDay = StartLocal.AddSeconds(x0).Date != StartLocal.AddSeconds(x1).Date;
        string fmt = multiDay ? (step >= 60 ? "dd/MM HH:mm" : "dd/MM HH:mm:ss") : step >= 60 ? "HH:mm" : "HH:mm:ss";
        for (double t = Math.Ceiling(x0 / step) * step; t <= x1; t += step)
        {
            double x = Math.Round(Px(t)) + 0.5;
            dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom - ZeroPad));
            var ft = Text(StartLocal.AddSeconds(t).ToString(fmt, CultureInfo.InvariantCulture), 10.5, AxisBrush, dpi);
            if (x - ft.Width / 2 > plot.Left - 20 && x + ft.Width / 2 < w)
                dc.DrawText(ft, new Point(x - ft.Width / 2, plot.Bottom + 4));
        }

        // Serie
        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 2, plot.Width, plot.Height + 2)));
        foreach (var (s, mn, mx, brk) in buckets)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                bool open = false;
                for (int c = 0; c < cols; c++)
                {
                    if (float.IsNaN(mn[c])) continue;
                    double x = plot.Left + c * plot.Width / (cols - 1);
                    if (!open || brk[c]) { ctx.BeginFigure(new Point(x, Py(mx[c])), false, false); open = true; }
                    else ctx.LineTo(new Point(x, Py(mx[c])), true, true);
                    if (mn[c] != mx[c]) ctx.LineTo(new Point(x, Py(mn[c])), true, true);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, s.Pen, geo);
        }
        dc.Pop();
        dc.DrawRectangle(null, FramePen, plot);

        // Cursore con i valori del punto più vicino
        if (_mouse is { } m && plot.Contains(m) && !_dragging)
        {
            double xv = x0 + (m.X - plot.Left) / plot.Width * span;
            dc.DrawLine(CursorPen, new Point(m.X, plot.Top), new Point(m.X, plot.Bottom));
            var lines = new List<(string, Brush)>();
            string? when = null;
            foreach (var s in Series)
            {
                int i = (int)Math.Round(xv / s.Step);
                if (i < 0 || i >= s.Count) continue;
                when ??= StartLocal.AddSeconds(i * s.Step).ToString(s.Step >= 60 ? "dd/MM HH:mm" : "dd/MM HH:mm:ss") + (s.Step == 60 ? " " + Core.L.T("(minuto)") : "");
                lines.Add(($"{s.Name}: {Fmt(s[i])} {Unit}", s.Brush));
            }
            lines.Insert(0, (when ?? StartLocal.AddSeconds(xv).ToString("dd/MM HH:mm:ss"), TitleBrush));
            var fts = lines.Select(l => Text(l.Item1, 11, l.Item2, dpi)).ToList();
            double tw = fts.Max(f => f.Width) + 12, th = fts.Sum(f => f.Height) + 8;
            double tx = m.X + 12 + tw > w ? m.X - 12 - tw : m.X + 12;
            dc.DrawRoundedRectangle(TipBg, FramePen, new Rect(tx, plot.Top + 4, tw, th), 4, 4);
            double ty = plot.Top + 8;
            foreach (var f in fts) { dc.DrawText(f, new Point(tx + 6, ty)); ty += f.Height; }
        }
    }

    private static double NiceCeil(double v)
    {
        double e = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double f = v / e;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * e;
    }

    private static double NiceTimeStep(double approx)
    {
        double[] steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400, 172800, 604800];
        foreach (var s in steps) if (s >= approx) return s;
        return steps[^1];
    }

    private static string Fmt(double v) =>
        double.IsNaN(v) ? "n/d" :
        v >= 10000 ? (v / 1000).ToString("0.#", CultureInfo.CurrentCulture) + "k" :
        v >= 100 ? v.ToString("0", CultureInfo.CurrentCulture) :
        v >= 10 ? v.ToString("0.#", CultureInfo.CurrentCulture) : v.ToString("0.##", CultureInfo.CurrentCulture);

    private static FormattedText Text(string s, double size, Brush b, double dpi, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, b, dpi);

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double t) { var p = new Pen(new SolidColorBrush(c), t); p.Freeze(); return p; }
}
