using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DnsLoadTester.App.Controls;
using DnsLoadTester.Core;
using DnsLoadTester.Core.Analysis;
using DnsLoadTester.Core.Dns;
using DnsLoadTester.Core.Output;
using DnsLoadTester.Core.Stats;
using Microsoft.Win32;

namespace DnsLoadTester.App;

/// <summary>
/// Analisi grafica a posteriori dei file queries_NNNN.csv: dispersione delle singole query, aggregati con risoluzione
/// adattiva all'intervallo visibile, filtri per nome, tipo ed esito, tabelle ed esportazione della selezione.
/// </summary>
public partial class QueryLogWindow : Window
{
    private QueryLog? _log;
    private int[] _rows = [];
    private double _viewStart, _viewEnd = 60;
    private CancellationTokenSource? _loadCts;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private int _version;
    private int _filterVersion;
    private CancellationTokenSource? _recomputeCts;
    private const int MaxListedRows = 50_000;
    private const double MinViewSeconds = 1;

    public QueryLogWindow(string? sessionFolder = null)
    {
        InitializeComponent();
        ChartQps.Title = L.T("Throughput"); ChartQps.Unit = L.T("query/s");
        ChartLat.Title = L.T("Latenza (solo risposte)"); ChartLat.Unit = "ms";
        ChartFail.Title = L.T("Fallimenti"); ChartFail.Unit = "%"; ChartFail.YCeiling = 100;
        Scatter.Title = L.T("Singole query");
        foreach (var ch in new TimeSeriesChart[] { ChartQps, ChartLat, ChartFail })
        {
            ch.ViewRequested += SetView;
            ch.LiveRequested += ShowAll;
        }
        Scatter.ViewRequested += SetView;
        Scatter.ResetRequested += ShowAll;
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RecomputeAsync(); };
        TxtInfo.Text = L.T("Caricare uno o più file queries_NNNN.csv di una sessione.");
        Loaded += async (_, _) =>
        {
            if (sessionFolder == null || !Directory.Exists(sessionFolder)) return;
            IReadOnlyList<string> files;
            try { files = QueryLog.SessionFiles(sessionFolder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Warn(L.F("Impossibile leggere il log: {0}", ex.Message));
                return;
            }
            if (files.Count > 0) await LoadAsync(files);
        };
        Closed += (_, _) => { _loadCts?.Cancel(); _recomputeCts?.Cancel(); };
    }

    // =====================================================================================
    // Caricamento
    // =====================================================================================

    private async void BtnOpenFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = L.T("Apri log per query"),
            Filter = L.T("Log per query (queries_*.csv)|queries_*.csv|File CSV (*.csv)|*.csv|Tutti i file|*.*"),
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) await LoadAsync(dlg.FileNames);
    }

    private async void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = L.T("Seleziona la cartella di una sessione") };
        if (dlg.ShowDialog(this) != true) return;
        IReadOnlyList<string> files;
        try { files = QueryLog.SessionFiles(dlg.FolderName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn(L.F("Impossibile leggere il log: {0}", ex.Message));
            return;
        }
        if (files.Count == 0) { Warn(L.T("La cartella non contiene file queries_*.csv (log per query disattivato in quella sessione?).")); return; }
        await LoadAsync(files);
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => _loadCts?.Cancel();

    private async Task LoadAsync(IReadOnlyList<string> files)
    {
        long bytes;
        try { bytes = files.Sum(f => new FileInfo(f).Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn(L.F("Impossibile leggere il log: {0}", ex.Message));
            return;
        }
        if (bytes > 2L * 1024 * 1024 * 1024 &&
            MessageBox.Show(this, L.F("I file selezionati occupano {0:N1} GB (circa {1:N0} milioni di query): il caricamento richiederà molta memoria e qualche minuto. Continuare?",
                    bytes / 1e9, bytes / 110 / 1e6),
                L.T("Analisi del log per query"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        SetBusy(true);
        var progress = new Progress<(long Lines, string File)>(p => TxtStatus.Text = L.F("Lettura di {0}: {1:N0} query…", p.File, p.Lines));
        try
        {
            var log = await Task.Run(() => QueryLog.Load(files, progress, cts.Token));
            // indici di riga del log precedente: mai abbinarli al nuovo log, nemmeno per un istante
            _recomputeCts?.Cancel();
            _rows = [];
            Scatter.Rows = [];
            _log = log;
            FillFilterCombos(log);
            TxtInfo.Text = L.F("{0} file · {1:N0} query · dal {2:dd/MM/yyyy HH:mm:ss} al {3:dd/MM/yyyy HH:mm:ss}",
                log.Files.Count, log.Count, log.StartLocal, log.StartLocal.AddSeconds(log.SpanSeconds));
            TxtStatus.Text = log.SkippedLines > 0
                ? L.F("Caricamento completato: {0:N0} righe incomplete ignorate.", log.SkippedLines)
                : L.T("Caricamento completato.");
            Title = L.T("Analisi del log per query") + " – " + Path.GetFileName(Path.GetDirectoryName(files[0]));
            PanelFilter.IsEnabled = true;
            BtnExport.IsEnabled = true;
            Scatter.Log = log;
            _viewStart = 0; _viewEnd = log.SpanSeconds + 1;
            await ApplyFilterAsync();
        }
        catch (OperationCanceledException) { TxtStatus.Text = L.T("Caricamento annullato."); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            TxtStatus.Text = "";
            Warn(L.F("Impossibile leggere il log: {0}", ex.Message));
        }
        catch (OutOfMemoryException)
        {
            TxtStatus.Text = "";
            Warn(L.T("Memoria insufficiente per caricare tutti i file: selezionarne meno (ad esempio un solo file queries_NNNN.csv)."));
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        BtnOpenFiles.IsEnabled = BtnOpenFolder.IsEnabled = !busy;
        BtnCancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Cursor = busy ? Cursors.AppStarting : null;
    }

    private sealed record ComboEntry(string Label, object? Value)
    {
        public override string ToString() => Label;
    }

    private void FillFilterCombos(QueryLog log)
    {
        CmbType.Items.Clear();
        CmbType.Items.Add(new ComboEntry(L.T("Tutti"), null));
        foreach (var t in log.QType.GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key))
            CmbType.Items.Add(new ComboEntry(DnsTypes.ToName(t), t));
        CmbType.SelectedIndex = 0;

        CmbOutcome.Items.Clear();
        CmbOutcome.Items.Add(new ComboEntry(L.T("Tutti"), OutcomeFilter.All));
        CmbOutcome.Items.Add(new ComboEntry(L.T("Solo successi"), OutcomeFilter.SuccessOnly));
        CmbOutcome.Items.Add(new ComboEntry(L.T("Solo fallimenti"), OutcomeFilter.FailuresOnly));
        var counts = new long[log.Outcomes.Count];
        foreach (var o in log.OutcomeId) counts[o]++;
        foreach (var i in Enumerable.Range(0, log.Outcomes.Count).OrderByDescending(i => counts[i]))
            CmbOutcome.Items.Add(new ComboEntry(log.Outcomes[i], log.Outcomes[i]));
        CmbOutcome.SelectedIndex = 0;
    }

    // =====================================================================================
    // Filtro
    // =====================================================================================

    private QueryFilter CurrentFilter()
    {
        var oc = (CmbOutcome.SelectedItem as ComboEntry)?.Value;
        return new QueryFilter
        {
            NameContains = TxtName.Text,
            Type = (CmbType.SelectedItem as ComboEntry)?.Value as ushort?,
            Mode = oc is OutcomeFilter m ? m : oc is string ? OutcomeFilter.Specific : OutcomeFilter.All,
            Outcome = oc as string,
            NxDomainIsSuccess = ChkNx.IsChecked == true,
        };
    }

    private async void BtnApply_Click(object sender, RoutedEventArgs e) => await ApplyFilterAsync();

    private async void BtnResetFilter_Click(object sender, RoutedEventArgs e)
    {
        TxtName.Text = "";
        CmbType.SelectedIndex = 0;
        CmbOutcome.SelectedIndex = 0;
        ChkNx.IsChecked = true;
        await ApplyFilterAsync();
    }

    private async void Filter_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await ApplyFilterAsync(); }
    }

    private async Task ApplyFilterAsync()
    {
        var log = _log;
        if (log == null) return;
        var f = CurrentFilter();
        int filterVersion = ++_filterVersion;
        TxtStatus.Text = L.T("Applicazione del filtro…");
        var rows = await Task.Run(() => QueryLogAnalyzer.Filter(log, f));
        if (filterVersion != _filterVersion || !ReferenceEquals(log, _log)) return;   // superato da un filtro o da un log più recente
        _rows = rows;
        Scatter.Rows = rows;
        TxtMatch.Text = L.F("{0:N0} query selezionate su {1:N0}", rows.Length, log.Count);
        TxtStatus.Text = "";
        SetView(_viewStart, _viewEnd);
        _debounce.Stop();
        await RecomputeAsync();
    }

    private void ChkLog_Changed(object sender, RoutedEventArgs e)
    {
        if (Scatter == null) return;
        Scatter.LogScale = ChkLog.IsChecked == true;
        Scatter.Refresh();
    }

    // =====================================================================================
    // Vista
    // =====================================================================================

    private double Total => (_log?.SpanSeconds ?? 60) + 1;

    private void SetView(double start, double end)
    {
        double total = Total;
        double width = Math.Clamp(end - start, MinViewSeconds, total);
        start = Math.Clamp(start, 0, Math.Max(0, total - width));
        _viewStart = start; _viewEnd = start + width;

        Scatter.ViewStart = start; Scatter.ViewEnd = start + width; Scatter.Refresh();
        foreach (var ch in new[] { ChartQps, ChartLat, ChartFail })
        {
            ch.StartLocal = _log?.StartLocal ?? DateTime.Now;
            ch.ViewStart = start; ch.ViewEnd = start + width; ch.DataEnd = total;
            ch.Refresh();
        }
        double max = Math.Max(0, total - width);
        Scroll.Minimum = 0; Scroll.Maximum = max;
        Scroll.ViewportSize = Math.Max(MinViewSeconds, width);
        Scroll.SmallChange = Math.Max(0.1, width / 20); Scroll.LargeChange = Math.Max(0.1, width * 0.9);
        Scroll.Value = start;
        Scroll.IsEnabled = max > 0;
        if (_log != null)
            TxtView.Text = $"{_log.StartLocal.AddSeconds(start):dd/MM HH:mm:ss} → {_log.StartLocal.AddSeconds(start + width):dd/MM HH:mm:ss}  ·  {StatsCollector.FormatDuration(TimeSpan.FromSeconds(width))}";
        _debounce.Stop();
        _debounce.Start();
    }

    private void ShowAll() => SetView(0, Total);
    private void BtnAll_Click(object sender, RoutedEventArgs e) => ShowAll();
    private void Scroll_Scroll(object sender, ScrollEventArgs e) => SetView(e.NewValue, e.NewValue + (_viewEnd - _viewStart));

    // =====================================================================================
    // Aggregati e tabelle (ricalcolati sull'intervallo visibile, in background)
    // =====================================================================================

    private async Task RecomputeAsync()
    {
        var log = _log;
        if (log == null) return;
        int version = ++_version;
        _recomputeCts?.Cancel();
        var cts = _recomputeCts = new CancellationTokenSource();
        var ct = cts.Token;
        var rows = _rows;
        double start = _viewStart, end = _viewEnd, width = end - start;
        double bucket = QueryLogAnalyzer.ChooseBucket(width, 1200);
        bool nx = ChkNx.IsChecked == true;
        int tab = Tabs.SelectedIndex;
        // si aggrega anche mezza finestra prima e dopo: lo scorrimento non mostra subito vuoti
        Core.Analysis.Aggregates agg; int a, b;
        List<Core.Analysis.OutcomeRow>? outcomes; List<Core.Analysis.NameRow>? names; List<RowItem>? list;
        try
        {
            (agg, a, b, outcomes, names, list) = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var agg = QueryLogAnalyzer.Aggregate(log, rows, Math.Max(0, start - width / 2), end + width / 2, bucket, nx);
                ct.ThrowIfCancellationRequested();
                int a = QueryLogAnalyzer.LowerBound(log, rows, start), b = QueryLogAnalyzer.LowerBound(log, rows, end);
                var outcomes = tab == 0 ? QueryLogAnalyzer.Outcomes(log, rows, a, b) : null;
                ct.ThrowIfCancellationRequested();
                var names = tab == 1 ? QueryLogAnalyzer.PerName(log, rows, a, b, nx) : null;
                List<RowItem>? list = null;
                if (tab == 2)
                {
                    list = new List<RowItem>(Math.Min(MaxListedRows, b - a));
                    for (int k = a; k < b && list.Count < MaxListedRows; k++) list.Add(RowItem.From(log, rows[k]));
                }
                return (agg, a, b, outcomes, names, list);
            }, ct);
        }
        catch (OperationCanceledException) { return; }
        if (version != _version || !ReferenceEquals(log, _log)) return;    // nel frattempo la vista è cambiata

        ShowAggregates(agg);
        int inView = b - a;
        if (outcomes != null)
            GridOutcomes.ItemsSource = outcomes.Select(o => new OutcomeItem(o.Outcome, o.Count.ToString("N0", L.Culture), o.Percent.ToString("F3", L.Culture) + "%")).ToList();
        if (names != null)
            GridNames.ItemsSource = names.Select(n => new NameItem(n.Name, n.Count.ToString("N0", L.Culture), Pct(n.SuccessPct, 2),
                n.Timeouts.ToString("N0", L.Culture), n.Errors.ToString("N0", L.Culture), Ms(n.AvgMs), Ms(n.P95Ms), Pct(n.AaPct, 0))).ToList();
        if (list != null)
            GridRows.ItemsSource = list;
        TxtStatus.Text = bucket >= 1
            ? L.F("Intervallo visibile: {0:N0} query · aggregazione dei grafici ogni {1}", inView, BucketLabel(bucket))
            : L.F("Intervallo visibile: {0:N0} query", inView);
        if (tab == 2 && inView > MaxListedRows)
            TxtStatus.Text += " · " + L.F("elenco limitato alle prime {0:N0}: restringere l'intervallo o il filtro", MaxListedRows);
    }

    private static string BucketLabel(double s) =>
        s < 60 ? L.F("{0} s", s) : s < 3600 ? L.F("{0} min", s / 60) : L.F("{0} h", s / 3600);

    private void ShowAggregates(Aggregates a)
    {
        double step = a.Bucket;
        ChartSeries S(string name, Color c, bool dashed = false) => new(name, c, step, dashed);
        var qps = S(L.T("Query/s"), Color.FromRgb(0x25, 0x63, 0xEB));
        var ok = S(L.T("Successi/s"), Color.FromRgb(0x16, 0xA3, 0x4A));
        var p50 = S("p50", Color.FromRgb(0x16, 0xA3, 0x4A));
        var p95 = S("p95", Color.FromRgb(0xF5, 0x9E, 0x0B));
        var p99 = S("p99", Color.FromRgb(0xDC, 0x26, 0x26));
        var fail = S(L.T("% fallite"), Color.FromRgb(0xDC, 0x26, 0x26));
        var tout = S(L.T("% timeout"), Color.FromRgb(0x7C, 0x3A, 0xED));
        for (int k = 0; k < a.Length; k++)
        {
            long ix = a.FirstIndex + k;
            double n = a.Completed[k];
            qps.Set(ix, n / step);
            ok.Set(ix, a.Success[k] / step);
            p50.Set(ix, a.P50[k]); p95.Set(ix, a.P95[k]); p99.Set(ix, a.P99[k]);
            fail.Set(ix, n == 0 ? double.NaN : 100.0 * (n - a.Success[k]) / n);
            tout.Set(ix, n == 0 ? double.NaN : 100.0 * a.Timeouts[k] / n);
        }
        Replace(ChartQps, qps, ok);
        Replace(ChartLat, p50, p95, p99);
        Replace(ChartFail, fail, tout);
    }

    private static void Replace(TimeSeriesChart ch, params ChartSeries[] s)
    {
        ch.Series.Clear();
        ch.Series.AddRange(s);
        ch.Refresh();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == Tabs && _log != null) _ = RecomputeAsync();
    }

    // =====================================================================================
    // Esportazione
    // =====================================================================================

    private async void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var log = _log;
        var rows = _rows;
        if (log == null) return;
        int a = QueryLogAnalyzer.LowerBound(log, rows, _viewStart), b = QueryLogAnalyzer.LowerBound(log, rows, _viewEnd);
        if (b <= a) { Warn(L.T("Nessuna query da esportare nell'intervallo visibile.")); return; }
        var dlg = new SaveFileDialog
        {
            Title = L.T("Esporta selezione"),
            Filter = L.T("File CSV (*.csv)|*.csv"),
            FileName = $"queries_selezione_{log.StartLocal.AddSeconds(_viewStart):yyyyMMdd_HHmmss}_v1.0.csv",
        };
        if (log.Files.Count > 0) dlg.InitialDirectory = Path.GetDirectoryName(log.Files[0]);
        if (dlg.ShowDialog(this) != true) return;
        string path = dlg.FileName;
        BtnExport.IsEnabled = false;
        TxtStatus.Text = L.T("Esportazione in corso…");
        try
        {
            await Task.Run(() => QueryLogAnalyzer.Export(log, rows, a, b, path, CsvDialect.ExcelItalian));
            TxtStatus.Text = L.F("Esportate {0:N0} query in {1}", b - a, path);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "";
            Warn(L.F("Esportazione non riuscita: {0}", ex.Message));
        }
        finally { BtnExport.IsEnabled = _log != null; }
    }

    private void Warn(string msg) => MessageBox.Show(this, msg, L.T("Analisi del log per query"), MessageBoxButton.OK, MessageBoxImage.Warning);

    private static string Pct(double v, int d) => double.IsNaN(v) ? "—" : v.ToString("F" + d, L.Culture) + "%";
    private static string Ms(double v) => double.IsNaN(v) ? "—" : v.ToString(v < 10 ? "F2" : "F1", L.Culture) + " ms";

    public sealed record OutcomeItem(string Name, string Count, string Pct);
    public sealed record NameItem(string Name, string Count, string Success, string Timeouts, string Errors, string Avg, string P95, string Aa);

    public sealed record RowItem(string Time, string Name, string Type, string Outcome, string Latency, string Aa, string Tc, string Worker, string Detail)
    {
        public static RowItem From(QueryLog log, int i)
        {
            bool resp = log.HasResponse(i);
            return new RowItem(
                log.TimeOf(i).ToString("dd/MM/yyyy HH:mm:ss.fff"),
                log.Names[log.NameId[i]],
                DnsTypes.ToName(log.QType[i]),
                log.Outcomes[log.OutcomeId[i]],
                log.LatMs[i].ToString("F2", L.Culture),
                resp ? (log.IsAuthoritative(i) ? "1" : "0") : "",
                resp ? (log.IsTruncated(i) ? "1" : "0") : "",
                log.Worker[i] >= 0 ? log.Worker[i].ToString() : "",
                log.DetailId[i] >= 0 ? log.Details[log.DetailId[i]] : "");
        }
    }
}
