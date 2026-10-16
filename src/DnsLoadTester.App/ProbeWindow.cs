using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DnsLoadTester.Core;
using DnsLoadTester.Core.Dns;
using DnsLoadTester.Core.Engine;
using DnsLoadTester.Core.Stats;

namespace DnsLoadTester.App;

/// <summary>Esito della verifica preliminare: una riga per query inviata.</summary>
public sealed class ProbeWindow : Window
{
    public sealed record Row(string Query, string Type, string Outcome, string Latency, string Aa, string Answers, string Bytes, string Detail);

    public ProbeWindow(TestConfig cfg, string resolved, List<QueryResult> results, List<string> warnings)
    {
        Title = L.T("Verifica destinazione");
        Width = 980; Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        Background = Brushes.White;

        var it = L.Culture;
        int ok = results.Count(r => r.IsSuccess(cfg.NxDomainIsSuccess));
        int aa = results.Count(r => r.IsResponse && r.Authoritative);
        int resp = results.Count(r => r.IsResponse);

        var head = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 12, 14, 8), FontSize = 13 };
        head.Inlines.Add(new System.Windows.Documents.Run($"{cfg.TargetDescription}  →  {resolved}") { FontWeight = FontWeights.SemiBold });
        head.Inlines.Add(new System.Windows.Documents.LineBreak());
        string verdict = L.F("{0} risposte valide su {1}", ok, results.Count);
        if (resp > 0) verdict += " · " + (aa == resp ? L.T("tutte con flag AA (risposte autoritative)")
            : L.F("flag AA in {0} su {1}: il server potrebbe non essere autoritativo per queste zone", aa, resp));
        head.Inlines.Add(new System.Windows.Documents.Run(verdict) { Foreground = ok == results.Count ? Brushes.DarkGreen : Brushes.DarkOrange });
        // Diagnosi delle situazioni più frequenti
        int refused = results.Count(r => r.IsResponse && r.Rcode == DnsRcodes.Refused);
        string? hint = null;
        if (refused > 0 && refused >= results.Count / 2)
        {
            hint = !cfg.RecursionDesired
                ? L.T("Il server rifiuta le query non ricorsive (RD=0): è quasi certamente un resolver ricorsivo, non un autoritativo. " +
                      "Per provarlo attivare «Flag RD» nella sezione Query. Per un autoritativo, usare invece nomi delle zone che ospita.")
                : L.T("Il server rifiuta anche le query ricorsive: probabilmente non accetta richieste da questo indirizzo IP (ACL) " +
                      "oppure è un autoritativo che non ospita queste zone.");
        }
        else if (resp > 0 && aa == 0 && !cfg.RecursionDesired && ok > 0)
            hint = L.T("Le risposte arrivano senza flag AA: il server non è autoritativo per queste zone (risponde da cache o come resolver).");
        if (hint != null)
        {
            head.Inlines.Add(new System.Windows.Documents.LineBreak());
            head.Inlines.Add(new System.Windows.Documents.Run("➜ " + hint) { Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8)), FontSize = 12 });
        }
        foreach (var w in warnings)
        {
            head.Inlines.Add(new System.Windows.Documents.LineBreak());
            head.Inlines.Add(new System.Windows.Documents.Run("⚠ " + w) { Foreground = Brushes.DarkOrange, FontSize = 11.5 });
        }

        var grid = new DataGrid
        {
            ItemsSource = results.Select(r => new Row(r.QName, DnsTypes.ToName(r.QType), r.OutcomeLabel,
                (r.LatencyUs / 1000.0).ToString("F2", it), r.IsResponse ? (r.Authoritative ? L.T("sì") : L.T("no")) : "",
                r.IsResponse ? r.Answers.ToString() : "", r.IsResponse ? r.ResponseBytes.ToString() : "", r.Detail ?? "")).ToList(),
            Margin = new Thickness(10, 0, 10, 10),
        };
        void Col(string h, string p, double w) => grid.Columns.Add(new DataGridTextColumn
        {
            Header = h, Binding = new Binding(p), Width = w > 0 ? new DataGridLength(w) : new DataGridLength(1, DataGridLengthUnitType.Star),
        });
        Col(L.T("Nome"), nameof(Row.Query), 230); Col(L.T("Tipo"), nameof(Row.Type), 60); Col(L.T("Esito"), nameof(Row.Outcome), 110);
        Col(L.T("Latenza ms"), nameof(Row.Latency), 80); Col("AA", nameof(Row.Aa), 40); Col(L.T("Risposte"), nameof(Row.Answers), 65);
        Col(L.T("Byte"), nameof(Row.Bytes), 55); Col(L.T("Dettaglio"), nameof(Row.Detail), 0);

        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(grid);
        Content = dock;
    }
}
