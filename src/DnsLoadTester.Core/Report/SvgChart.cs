using System.Globalization;
using System.Net;
using System.Text;

namespace DnsLoadTester.Core.Report;

public sealed record ChartSeries(string Name, string Color, double[] Values, bool Dashed = false);

/// <summary>Grafici SVG statici per il report HTML (nessuna dipendenza esterna).</summary>
public static class SvgChart
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v) => v.ToString("0.##", Inv);

    public static string Line(string title, IReadOnlyList<DateTime> xs, IReadOnlyList<ChartSeries> series, string yUnit,
        double? yMaxFixed = null, double yMin = 0)
    {
        const int W = 1000, H = 280, L = 70, R = 44, T = 34, B = 46;
        int pw = W - L - R, ph = H - T - B;
        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {W} {H}\" class=\"chart\" role=\"img\" aria-label=\"{Esc(title)}\">");
        sb.Append($"<text x=\"{L}\" y=\"20\" class=\"ct\">{Esc(title)}</text>");
        if (xs.Count < 2)
        {
            sb.Append($"<text x=\"{W / 2}\" y=\"{H / 2}\" class=\"na\" text-anchor=\"middle\">{Esc(Core.L.T("Dati insufficienti"))}</text></svg>");
            return sb.ToString();
        }

        double yMax = yMaxFixed ?? series.SelectMany(s => s.Values).Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).DefaultIfEmpty(1).Max();
        yMax = NiceCeil(Math.Max(yMax, yMin + 1e-9) * 1.05);
        if (yUnit == "%" && yMax > 100) yMax = 100;
        if (yMax <= yMin) yMax = yMin + 1;
        double t0 = xs[0].Ticks, t1 = xs[^1].Ticks;
        if (t1 <= t0) t1 = t0 + TimeSpan.TicksPerSecond;
        double X(DateTime d) => L + (d.Ticks - t0) / (t1 - t0) * pw;
        double Y(double v) => T + ph - (Math.Clamp(v, yMin, yMax) - yMin) / (yMax - yMin) * ph;

        // griglia orizzontale
        for (int i = 0; i <= 5; i++)
        {
            double v = yMin + (yMax - yMin) * i / 5;
            double y = Y(v);
            sb.Append($"<line x1=\"{L}\" x2=\"{L + pw}\" y1=\"{F(y)}\" y2=\"{F(y)}\" class=\"grid\"/>");
            sb.Append($"<text x=\"{L - 8}\" y=\"{F(y + 4)}\" class=\"ax\" text-anchor=\"end\">{FormatNum(v)}</text>");
        }
        sb.Append($"<text x=\"14\" y=\"{T + ph / 2}\" class=\"ax\" transform=\"rotate(-90 14 {T + ph / 2})\" text-anchor=\"middle\">{Esc(yUnit)}</text>");

        // etichette tempo
        var span = xs[^1] - xs[0];
        string fmt = span.TotalDays > 2 ? "dd/MM HH:mm" : span.TotalHours > 6 ? "dd/MM HH:mm" : span.TotalMinutes > 10 ? "HH:mm" : "HH:mm:ss";
        for (int i = 0; i <= 6; i++)
        {
            var d = new DateTime((long)(t0 + (t1 - t0) * i / 6));
            double x = X(d);
            sb.Append($"<line x1=\"{F(x)}\" x2=\"{F(x)}\" y1=\"{T}\" y2=\"{T + ph}\" class=\"gridv\"/>");
            sb.Append($"<text x=\"{F(x)}\" y=\"{T + ph + 18}\" class=\"ax\" text-anchor=\"middle\">{d.ToString(fmt, Inv)}</text>");
        }
        sb.Append($"<rect x=\"{L}\" y=\"{T}\" width=\"{pw}\" height=\"{ph}\" class=\"frame\"/>");

        // serie
        foreach (var s in series)
        {
            var path = new StringBuilder();
            bool pen = false;
            for (int i = 0; i < xs.Count && i < s.Values.Length; i++)
            {
                double v = s.Values[i];
                if (double.IsNaN(v) || double.IsInfinity(v)) { pen = false; continue; }
                path.Append(pen ? 'L' : 'M').Append(F(X(xs[i]))).Append(',').Append(F(Y(v))).Append(' ');
                pen = true;
            }
            string dash = s.Dashed ? " stroke-dasharray=\"5 4\"" : "";
            sb.Append($"<path d=\"{path}\" fill=\"none\" stroke=\"{s.Color}\" stroke-width=\"1.6\"{dash} stroke-linejoin=\"round\"/>");
        }

        // legenda
        double lx = L + 260;
        foreach (var s in series)
        {
            sb.Append($"<line x1=\"{F(lx)}\" x2=\"{F(lx + 18)}\" y1=\"15\" y2=\"15\" stroke=\"{s.Color}\" stroke-width=\"3\"/>");
            sb.Append($"<text x=\"{F(lx + 24)}\" y=\"19\" class=\"lg\">{Esc(s.Name)}</text>");
            lx += 34 + s.Name.Length * 7.2;
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Striscia colorata dello stato di salute nel tempo.</summary>
    public static string HealthStrip(IReadOnlyList<DateTime> xs, IReadOnlyList<int> states, TimeSpan step)
    {
        const int W = 1000, H = 70, L = 70, R = 20;
        int pw = W - L - R;
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {W} {H}\" class=\"chart\" role=\"img\" aria-label=\"{Esc(Core.L.T("Stato del server nel tempo"))}\">");
        sb.Append($"<text x=\"{L}\" y=\"16\" class=\"ct\">{Esc(Core.L.T("Stato del server nel tempo"))}</text>");
        if (xs.Count == 0) return sb.Append("</svg>").ToString();
        double t0 = xs[0].Ticks, t1 = (xs[^1] + step).Ticks;
        double X(DateTime d) => L + (d.Ticks - t0) / (t1 - t0) * pw;
        string[] colors = ["#9ca3af", "#16a34a", "#f59e0b", "#dc2626"];
        int i = 0;
        while (i < xs.Count)
        {
            int j = i;
            while (j + 1 < xs.Count && states[j + 1] == states[i]) j++;
            double x0 = X(xs[i]), x1 = X(xs[j] + step);
            sb.Append($"<rect x=\"{F(x0)}\" y=\"26\" width=\"{F(Math.Max(0.6, x1 - x0))}\" height=\"18\" fill=\"{colors[Math.Clamp(states[i], 0, 3)]}\"/>");
            i = j + 1;
        }
        string[] labels = [Core.L.T("Nessun dato"), Core.L.T("OK"), Core.L.T("Degradato"), Core.L.T("Non disponibile")];
        double lx = L;
        for (int k = 1; k < 4; k++)
        {
            sb.Append($"<rect x=\"{F(lx)}\" y=\"54\" width=\"12\" height=\"10\" fill=\"{colors[k]}\"/><text x=\"{F(lx + 16)}\" y=\"63\" class=\"lg\">{labels[k]}</text>");
            lx += 130;
        }
        sb.Append($"<rect x=\"{F(lx)}\" y=\"54\" width=\"12\" height=\"10\" fill=\"{colors[0]}\"/><text x=\"{F(lx + 16)}\" y=\"63\" class=\"lg\">{labels[0]}</text>");
        return sb.Append("</svg>").ToString();
    }

    /// <summary>Istogramma a barre verticali con etichette di categoria.</summary>
    public static string Bars(string title, IReadOnlyList<string> labels, IReadOnlyList<double> values, string color, string yUnit)
    {
        const int W = 1000, H = 260, L = 70, R = 20, T = 34, B = 50;
        int pw = W - L - R, ph = H - T - B;
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {W} {H}\" class=\"chart\" role=\"img\" aria-label=\"{Esc(title)}\">");
        sb.Append($"<text x=\"{L}\" y=\"20\" class=\"ct\">{Esc(title)}</text>");
        double max = NiceCeil(Math.Max(values.DefaultIfEmpty(0).Max(), 1e-9) * 1.1);
        for (int i = 0; i <= 4; i++)
        {
            double v = max * i / 4, y = T + ph - v / max * ph;
            sb.Append($"<line x1=\"{L}\" x2=\"{L + pw}\" y1=\"{F(y)}\" y2=\"{F(y)}\" class=\"grid\"/>");
            sb.Append($"<text x=\"{L - 8}\" y=\"{F(y + 4)}\" class=\"ax\" text-anchor=\"end\">{FormatNum(v)}</text>");
        }
        sb.Append($"<text x=\"14\" y=\"{T + ph / 2}\" class=\"ax\" transform=\"rotate(-90 14 {T + ph / 2})\" text-anchor=\"middle\">{Esc(yUnit)}</text>");
        int n = Math.Max(1, labels.Count);
        double bw = pw / (double)n;
        for (int i = 0; i < labels.Count; i++)
        {
            double h = values[i] / max * ph;
            double x = L + i * bw + bw * 0.12;
            sb.Append($"<rect x=\"{F(x)}\" y=\"{F(T + ph - h)}\" width=\"{F(bw * 0.76)}\" height=\"{F(Math.Max(0, h))}\" fill=\"{color}\" rx=\"2\"/>");
            if (values[i] > 0)
                sb.Append($"<text x=\"{F(x + bw * 0.38)}\" y=\"{F(T + ph - h - 4)}\" class=\"ax\" text-anchor=\"middle\">{FormatNum(values[i])}</text>");
            sb.Append($"<text x=\"{F(x + bw * 0.38)}\" y=\"{T + ph + 18}\" class=\"ax\" text-anchor=\"middle\">{Esc(labels[i])}</text>");
        }
        sb.Append($"<rect x=\"{L}\" y=\"{T}\" width=\"{pw}\" height=\"{ph}\" class=\"frame\"/>");
        return sb.Append("</svg>").ToString();
    }

    public static double NiceCeil(double v)
    {
        if (v <= 0) return 1;
        double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double f = v / exp;
        double nf = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
        return nf * exp;
    }

    public static string FormatNum(double v) =>
        Math.Abs(v) >= 1_000_000 ? (v / 1_000_000).ToString("0.#", Inv) + "M" :
        Math.Abs(v) >= 10_000 ? (v / 1000).ToString("0.#", Inv) + "k" :
        Math.Abs(v) >= 100 ? v.ToString("0", Inv) :
        Math.Abs(v) >= 10 ? v.ToString("0.#", Inv) : v.ToString("0.##", Inv);

    public static string Esc(string s) => WebUtility.HtmlEncode(s);
}
