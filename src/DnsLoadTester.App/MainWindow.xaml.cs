using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DnsLoadTester.App.Controls;
using DnsLoadTester.Core;
using DnsLoadTester.Core.Dns;
using DnsLoadTester.Core.Engine;
using DnsLoadTester.Core.Output;
using DnsLoadTester.Core.Report;
using DnsLoadTester.Core.Stats;
using Microsoft.Win32;

namespace DnsLoadTester.App;

public partial class MainWindow : Window
{
    private LoadEngine? _engine;
    private string? _lastSessionFolder, _lastReport;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<EventRow> _events = [];
    private long _nextSecond;
    private int _nextMinute;
    private bool _closingAfterStop;
    private int _tick;
    private static CultureInfo _it => L.Culture;

    // Dati dei grafici (intera sessione, al secondo e al minuto) e stato della vista.
    private ChartData _data = new();
    private const double SecondsResolutionMaxWindow = 7200;   // oltre 2 ore di finestra si passa ai dati al minuto
    private const double MinViewSeconds = 10;
    private double _viewWidth = 300;      // 0 = tutta la sessione
    private double _viewStart;
    private bool _follow = true;          // la vista segue il tempo reale
    private bool _suppressRangeEvents;
    private bool? _lastUseSeconds;

    // Sessione salvata aperta per l'analisi (in alternativa al test in corso).
    private LiveSnapshot? _loadedSnapshot;
    private TestConfig? _loadedConfig;

    private readonly UiHistory _history = UiHistory.Current;
    private string? _lastQueryFile;

    public MainWindow()
    {
        InitializeComponent();
        TxtVersion.Text = "v" + AppPaths.Version;
        Title = $"DNS Load Tester v{AppPaths.Version}";
        foreach (var t in DnsTypes.Common) CmbDefaultType.Items.Add(t);

        ChartQps.Title = L.T("Throughput"); ChartQps.Unit = L.T("query/s");
        ChartLat.Title = L.T("Latenza"); ChartLat.Unit = "ms";
        ChartFail.Title = L.T("Fallimenti"); ChartFail.Unit = "%"; ChartFail.YCeiling = 100;
        foreach (var ch in Charts)
        {
            ch.ViewRequested += OnChartViewRequested;
            ch.LiveRequested += GoLive;
        }
        CmbServer.ItemsSource = _history.Servers;
        CmbDohUrl.ItemsSource = _history.DohUrls;
        CmbTlsName.ItemsSource = _history.TlsNames;
        RefreshView();

        GridEvents.ItemsSource = _events;
        _timer.Tick += (_, _) => OnTick();

        LoadConfigToUi(LoadSettings());
        ConfigPanel.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateWarnings()));
        ConfigPanel.AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => UpdateWarnings()));
        ConfigPanel.AddHandler(System.Windows.Controls.Primitives.ToggleButton.ClickEvent, new RoutedEventHandler((_, _) => UpdateWarnings()));
        UpdateWarnings();
        SetUiState(EngineState.Idle);
        TxtStatus.Text = L.F("Cartella dati: {0}", AppPaths.DataFolder);
        if (App.LanguageMessages.Count > 0) TxtStatus.Text += " · " + string.Join(" · ", App.LanguageMessages);
        TxtQueriesHint.Text = L.T("Una per riga: «nome TIPO» (es. dominio.it SOA). {rnd} = etichetta casuale a ogni query (verifica NXDOMAIN). Righe con # ignorate.");
    }

    // =====================================================================================
    // Configurazione <-> interfaccia
    // =====================================================================================

    private static void SelectTag(ComboBox cb, string tag)
    {
        foreach (ComboBoxItem it in cb.Items)
            if (string.Equals(it.Tag as string, tag, StringComparison.OrdinalIgnoreCase)) { cb.SelectedItem = it; return; }
        cb.SelectedIndex = 0;
    }

    private static string ComboTag(ComboBox cb) => (cb.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private static T TagEnum<T>(ComboBox cb) where T : struct, Enum =>
        Enum.TryParse<T>(ComboTag(cb), true, out var v) ? v : default;

    private void LoadConfigToUi(TestConfig c)
    {
        SelectTag(CmbProtocol, c.Protocol.ToString());
        CmbServer.Text = c.Server;
        TxtPort.Text = c.Port.ToString();
        SelectTag(CmbIpFamily, c.IpFamily.ToString());
        CmbDohUrl.Text = c.DohUrl;
        SelectTag(CmbDohMethod, c.DohMethod.ToString());
        SelectTag(CmbHttpVersion, c.DohHttpVersion.ToString());
        CmbTlsName.Text = c.TlsServerName;
        ChkSkipCert.IsChecked = c.TlsSkipValidation;
        ChkReuse.IsChecked = c.ReuseConnections;

        TxtQueries.Text = string.Join(Environment.NewLine, c.Queries ?? []);
        CmbDefaultType.Text = c.DefaultQueryType;
        SelectTag(CmbOrder, c.Order.ToString());
        ChkRd.IsChecked = c.RecursionDesired;
        ChkEdns.IsChecked = c.UseEdns;
        TxtEdnsSize.Text = c.EdnsBufferSize.ToString();
        ChkDo.IsChecked = c.DnssecOk;

        TxtQps.Text = c.TargetQps.ToString();
        TxtConcurrency.Text = c.Concurrency.ToString();
        TxtTimeout.Text = c.TimeoutMs.ToString();
        TxtRamp.Text = c.RampUpSeconds.ToString();
        var d = TimeSpan.FromSeconds(c.DurationSeconds);
        TxtDays.Text = ((int)d.TotalDays).ToString();
        TxtHours.Text = d.Hours.ToString();
        TxtMinutes.Text = d.Minutes.ToString();
        ChkUntilStop.IsChecked = c.DurationSeconds == 0;

        TxtWindow.Text = c.HealthWindowSeconds.ToString();
        TxtDegradedPct.Text = c.DegradedSuccessPct.ToString(CultureInfo.CurrentCulture);
        TxtDegradedP95.Text = c.DegradedP95Ms.ToString();
        TxtDownPct.Text = c.DownSuccessPct.ToString(CultureInfo.CurrentCulture);
        ChkNxSuccess.IsChecked = c.NxDomainIsSuccess;

        TxtOutput.Text = string.IsNullOrWhiteSpace(c.OutputFolder) ? AppPaths.DefaultSessionsFolder : c.OutputFolder;
        TxtLabel.Text = c.SessionLabel;
        SelectTag(CmbLogMode, c.QueryLog.ToString());
        TxtRotate.Text = c.QueryLogRotateMb.ToString();
        SelectTag(CmbCsv, c.CsvDialect.ToString());
        UpdateProtocolPanels();
        ChkUntilStop_Changed(this, new RoutedEventArgs());
    }

    /// <summary>Legge la configurazione dai controlli; i campi numerici non validi finiscono in <paramref name="errors"/>.</summary>
    private TestConfig ReadConfigFromUi(List<string> errors)
    {
        int I(TextBox tb, string name, int def)
        {
            var s = tb.Text.Trim().Replace(".", "").Replace(" ", "");
            if (s.Length == 0) return def;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            errors.Add(L.F("Valore non numerico in «{0}».", name));
            return def;
        }
        double Dbl(TextBox tb, string name, double def)
        {
            var s = tb.Text.Trim().Replace(',', '.');
            if (s.Length == 0) return def;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            errors.Add(L.F("Valore non numerico in «{0}».", name));
            return def;
        }

        var c = new TestConfig
        {
            Protocol = TagEnum<DnsProtocol>(CmbProtocol),
            Server = CmbServer.Text.Trim(),
            Port = I(TxtPort, L.T("Porta"), 53),
            IpFamily = int.TryParse(ComboTag(CmbIpFamily), out var fam) ? fam : 0,
            DohUrl = CmbDohUrl.Text.Trim(),
            DohMethod = TagEnum<DohMethod>(CmbDohMethod),
            DohHttpVersion = TagEnum<DohHttpVersion>(CmbHttpVersion),
            TlsServerName = CmbTlsName.Text.Trim(),
            TlsSkipValidation = ChkSkipCert.IsChecked == true,
            ReuseConnections = ChkReuse.IsChecked == true,
            Queries = TxtQueries.Text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList(),
            DefaultQueryType = string.IsNullOrWhiteSpace(CmbDefaultType.Text) ? "A" : CmbDefaultType.Text.Trim().ToUpperInvariant(),
            Order = TagEnum<QueryOrder>(CmbOrder),
            RecursionDesired = ChkRd.IsChecked == true,
            UseEdns = ChkEdns.IsChecked == true,
            EdnsBufferSize = I(TxtEdnsSize, L.T("Buffer EDNS"), 1232),
            DnssecOk = ChkDo.IsChecked == true,
            TargetQps = I(TxtQps, L.T("QPS obiettivo"), 100),
            Concurrency = I(TxtConcurrency, L.T("Worker"), 10),
            TimeoutMs = I(TxtTimeout, L.T("Timeout"), 2000),
            RampUpSeconds = I(TxtRamp, L.T("Rampa"), 0),
            HealthWindowSeconds = I(TxtWindow, L.T("Finestra di osservazione"), 10),
            DegradedSuccessPct = Dbl(TxtDegradedPct, L.T("Soglia degradato"), 99),
            DegradedP95Ms = I(TxtDegradedP95, L.T("Soglia p95"), 500),
            DownSuccessPct = Dbl(TxtDownPct, L.T("Soglia non disponibile"), 50),
            NxDomainIsSuccess = ChkNxSuccess.IsChecked == true,
            OutputFolder = TxtOutput.Text.Trim(),
            SessionLabel = TxtLabel.Text.Trim(),
            QueryLog = TagEnum<QueryLogMode>(CmbLogMode),
            QueryLogRotateMb = I(TxtRotate, L.T("Rotazione file"), 200),
            CsvDialect = TagEnum<CsvDialect>(CmbCsv),
        };
        if (!DnsTypes.TryParse(c.DefaultQueryType, out _)) errors.Add(L.F("Tipo predefinito «{0}» non riconosciuto.", c.DefaultQueryType));
        c.DurationSeconds = ChkUntilStop.IsChecked == true ? 0
            : I(TxtDays, L.T("Giorni"), 0) * 86400L + I(TxtHours, L.T("Ore"), 0) * 3600L + I(TxtMinutes, L.T("Minuti"), 0) * 60L;
        if (ChkUntilStop.IsChecked != true && c.DurationSeconds <= 0) errors.Add(L.T("Indicare una durata o selezionare «Senza limite di durata»."));
        if (c.RampUpSeconds < 0) errors.Add(L.T("Rampa non valida."));
        if (c.DownSuccessPct > c.DegradedSuccessPct) errors.Add(L.T("La soglia «non disponibile» deve essere inferiore a quella «degradato»."));
        return c;
    }

    private void CmbProtocol_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || TxtPort == null) return;
        var p = TagEnum<DnsProtocol>(CmbProtocol);
        // Aggiorna la porta solo se era quella predefinita di un altro protocollo
        if (int.TryParse(TxtPort.Text, out var port) && port is 53 or 853 or 443)
            TxtPort.Text = TestConfig.DefaultPort(p).ToString();
        UpdateProtocolPanels();
    }

    private void UpdateProtocolPanels()
    {
        var p = TagEnum<DnsProtocol>(CmbProtocol);
        bool doh = p == DnsProtocol.DoH;
        PanelDoh.Visibility = doh ? Visibility.Visible : Visibility.Collapsed;
        PanelTls.Visibility = p is DnsProtocol.DoT or DnsProtocol.DoQ or DnsProtocol.DoH ? Visibility.Visible : Visibility.Collapsed;
        RowTlsName.Visibility = doh ? Visibility.Collapsed : Visibility.Visible;
        foreach (UIElement el in new UIElement[] { LblServer, RowServer, LblPort, RowPort })
            el.Visibility = doh ? Visibility.Collapsed : Visibility.Visible;
        ChkReuse.Visibility = p == DnsProtocol.Udp ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ChkUntilStop_Changed(object sender, RoutedEventArgs e)
    {
        if (TxtDays == null) return;
        bool en = ChkUntilStop.IsChecked != true;
        TxtDays.IsEnabled = TxtHours.IsEnabled = TxtMinutes.IsEnabled = en;
    }

    private void UpdateWarnings()
    {
        if (!IsInitialized || BdWarnings == null) return;
        var errs = new List<string>();
        var c = ReadConfigFromUi(errs);
        var w = c.Warnings();
        BdWarnings.Visibility = w.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TxtWarnings.Text = string.Join(Environment.NewLine + Environment.NewLine, w.Select(x => "⚠ " + x));
    }

    // =====================================================================================
    // Impostazioni e profili
    // =====================================================================================

    private static TestConfig LoadSettings()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile)) return TestConfig.FromJson(File.ReadAllText(AppPaths.SettingsFile));
        }
        catch { /* impostazioni corrotte: si riparte dai valori predefiniti */ }
        return new TestConfig
        {
            Server = "",
            Queries = [L.T("# Esempi (sostituire con le zone servite dal server autoritativo)"), "dominio.it SOA", "dominio.it NS", "www.dominio.it A", "{rnd}.dominio.it A"],
            OutputFolder = AppPaths.DefaultSessionsFolder,
        };
    }

    private static void SaveSettings(TestConfig c)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            SessionFiles.WriteAllTextAtomic(AppPaths.SettingsFile, c.ToJson());
        }
        catch { /* non critico */ }
    }

    private void BtnLoadProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = L.T("Profilo DNS Load Tester (*.json)|*.json|Tutti i file|*.*"), Title = L.T("Carica profilo") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var json = File.ReadAllText(dlg.FileName);
            // accetta sia un profilo sia un session.json di una sessione precedente
            var cfg = json.Contains("\"Config\"") && json.Contains("\"AppVersion\"")
                ? System.Text.Json.JsonSerializer.Deserialize<SessionInfo>(json, TestConfig.JsonOptions)!.Config
                : TestConfig.FromJson(json);
            LoadConfigToUi(cfg);
            TxtStatus.Text = L.F("Profilo caricato: {0}", dlg.FileName);
        }
        catch (Exception ex) { Error(L.F("Impossibile leggere il profilo: {0}", ex.Message)); }
    }

    private void BtnSaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var cfg = ReadConfigFromUi([]);
        var name = string.IsNullOrWhiteSpace(cfg.SessionLabel) ? L.T("profilo") : cfg.SessionLabel;
        var dlg = new SaveFileDialog { Filter = L.T("Profilo DNS Load Tester (*.json)|*.json"), FileName = $"{name}_v1.0.json", Title = L.T("Salva profilo") };
        if (dlg.ShowDialog(this) != true) return;
        try { File.WriteAllText(dlg.FileName, cfg.ToJson()); TxtStatus.Text = L.F("Profilo salvato: {0}", dlg.FileName); }
        catch (Exception ex) { Error(L.F("Impossibile salvare il profilo: {0}", ex.Message)); }
    }

    private void BtnLoadQueries_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = L.T("Elenchi di query (*.txt;*.csv)|*.txt;*.csv|Tutti i file|*.*"), Title = L.T("Carica elenco query") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var lines = File.ReadAllLines(dlg.FileName);
            if (lines.Length > 200_000 && MessageBox.Show(this, L.F("Il file contiene {0:N0} righe. Caricarle tutte?", lines.Length), L.T("Elenco query"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            TxtQueries.Text = string.Join(Environment.NewLine, lines);
            _lastQueryFile = dlg.FileName;
            QuerySet.Parse(lines, CmbDefaultType.Text, QueryOrder.RoundRobin, out var warns);
            TxtStatus.Text = warns.Count > 0
                ? L.F("Caricate {0:N0} righe da {1} ({2} avvisi)", lines.Length, Path.GetFileName(dlg.FileName), warns.Count)
                : L.F("Caricate {0:N0} righe da {1}", lines.Length, Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex) { Error(L.F("Impossibile leggere il file: {0}", ex.Message)); }
    }

    private void BtnSaveQueries_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = L.T("Elenco di query (*.txt)|*.txt|Tutti i file|*.*"),
            Title = L.T("Salva elenco query"),
            DefaultExt = ".txt",
        };
        if (_lastQueryFile != null)
        {
            // Propone la versione successiva del file caricato (es. _v1.0 → _v1.1)
            dlg.InitialDirectory = Path.GetDirectoryName(_lastQueryFile);
            dlg.FileName = NextVersionName(Path.GetFileName(_lastQueryFile));
        }
        else
        {
            var basis = !string.IsNullOrWhiteSpace(TxtLabel.Text) ? TxtLabel.Text
                : !string.IsNullOrWhiteSpace(CmbServer.Text) ? CmbServer.Text : L.T("elenco");
            foreach (var ch in Path.GetInvalidFileNameChars()) basis = basis.Replace(ch, '-');
            dlg.FileName = $"query_{basis.Trim().Replace(' ', '_').Replace(':', '-')}_v1.0.txt";
        }
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var text = TxtQueries.Text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            if (!text.EndsWith("\r\n")) text += "\r\n";
            File.WriteAllText(dlg.FileName, text, new System.Text.UTF8Encoding(false));
            _lastQueryFile = dlg.FileName;
            int n = TxtQueries.Text.Split('\n').Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#') && !l.TrimStart().StartsWith(';'));
            TxtStatus.Text = L.F("Elenco di {0} query salvato in {1}", n, dlg.FileName);
        }
        catch (Exception ex) { Error(L.F("Impossibile salvare il file: {0}", ex.Message)); }
    }

    /// <summary>"nome_v1.0.txt" → "nome_v1.1.txt"; senza versione aggiunge "_v1.0".</summary>
    private static string NextVersionName(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var m = System.Text.RegularExpressions.Regex.Match(stem, @"^(.*_v)(\d+)\.(\d+)$");
        return m.Success
            ? $"{m.Groups[1].Value}{m.Groups[2].Value}.{int.Parse(m.Groups[3].Value) + 1}{ext}"
            : $"{stem}_v1.0{(string.IsNullOrEmpty(ext) ? ".txt" : ext)}";
    }

    // =====================================================================================
    // Cronologia dei target
    // =====================================================================================

    private void PushHistory(TestConfig cfg)
    {
        if (cfg.Protocol == DnsProtocol.DoH) KeepText(CmbDohUrl, () => UiHistory.Push(_history.DohUrls, cfg.DohUrl));
        else KeepText(CmbServer, () => UiHistory.Push(_history.Servers, cfg.Server));
        if (cfg.Protocol is DnsProtocol.DoT or DnsProtocol.DoQ && !string.IsNullOrWhiteSpace(cfg.TlsServerName))
            KeepText(CmbTlsName, () => UiHistory.Push(_history.TlsNames, cfg.TlsServerName));
        _history.Save();
    }

    /// <summary>Modifica la lista di una ComboBox modificabile senza perdere il testo digitato.</summary>
    private static void KeepText(ComboBox cb, Action change)
    {
        var text = cb.Text;
        change();
        if (cb.Text != text) cb.Text = text;
    }

    private void BtnHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        bool doh = btn.Tag as string == "DohUrl";
        var cb = doh ? CmbDohUrl : CmbServer;
        var list = doh ? _history.DohUrls : _history.Servers;
        var current = cb.Text.Trim();

        var menu = new ContextMenu { PlacementTarget = btn, Placement = PlacementMode.Bottom };
        var miRemove = new MenuItem
        {
            Header = string.IsNullOrEmpty(current) ? L.T("Rimuovi la voce corrente dalla cronologia") : L.F("Rimuovi «{0}» dalla cronologia", current),
            IsEnabled = list.Any(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase)),
        };
        miRemove.Click += (_, _) => { KeepText(cb, () => UiHistory.Remove(list, current)); _history.Save(); };
        var miClear = new MenuItem { Header = doh ? L.T("Svuota la cronologia degli URL DoH") : L.T("Svuota la cronologia dei server"), IsEnabled = list.Count > 0 };
        miClear.Click += (_, _) => { KeepText(cb, list.Clear); _history.Save(); };
        var miClearAll = new MenuItem { Header = L.T("Svuota tutta la cronologia (server, URL, nomi TLS)") };
        miClearAll.Click += (_, _) =>
        {
            if (MessageBox.Show(this, L.T("Cancellare tutta la cronologia dei target?"), L.T("Cronologia"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            KeepText(CmbServer, _history.Servers.Clear);
            KeepText(CmbDohUrl, _history.DohUrls.Clear);
            KeepText(CmbTlsName, _history.TlsNames.Clear);
            _history.Save();
        };
        menu.Items.Add(new MenuItem { Header = L.F("{0} voci in cronologia (massimo {1})", list.Count, UiHistory.MaxEntries), IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(miRemove);
        menu.Items.Add(miClear);
        menu.Items.Add(miClearAll);
        menu.IsOpen = true;
    }

    private void BtnBrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = L.T("Cartella per le sessioni di test") };
        if (Directory.Exists(TxtOutput.Text)) dlg.InitialDirectory = TxtOutput.Text;
        if (dlg.ShowDialog(this) == true) TxtOutput.Text = dlg.FolderName;
    }

    // =====================================================================================
    // Avvio / arresto
    // =====================================================================================

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        var cfg = ReadConfigFromUi(errors);
        errors.AddRange(cfg.Validate());
        if (errors.Count > 0) { Error(string.Join(Environment.NewLine, errors.Distinct())); return; }
        try { Directory.CreateDirectory(cfg.OutputFolder); }
        catch (Exception ex) { Error(L.F("Cartella di output non utilizzabile: {0}", ex.Message)); return; }

        var dur = cfg.DurationSeconds > 0 ? StatsCollector.FormatDuration(TimeSpan.FromSeconds(cfg.DurationSeconds)) : L.T("fino ad arresto manuale");
        var load = cfg.TargetQps > 0 ? L.F("{0:N0} query/s con {1} worker", cfg.TargetQps, cfg.Concurrency) : L.F("ILLIMITATO con {0} worker", cfg.Concurrency);
        var msg = L.F("Avviare il test?\n\nDestinazione: {0}\nCarico: {1}\nDurata: {2}\n", cfg.TargetDescription, load, dur);
        var warns = cfg.Warnings();
        if (warns.Count > 0) msg += "\n" + L.T("Avvisi:") + "\n• " + string.Join("\n• ", warns) + "\n";
        msg += "\n" + L.T("Procedere solo su server propri o con autorizzazione del titolare.");
        if (MessageBox.Show(this, msg, L.T("Conferma avvio"), MessageBoxButton.OKCancel,
                warns.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK) return;

        SaveSettings(cfg);
        ResetDashboard();
        var engine = new LoadEngine(cfg, AppPaths.Version);
        engine.EventRaised += ev => Dispatcher.BeginInvoke(() => AddEvent(ev));
        engine.StateChanged += st => Dispatcher.BeginInvoke(() => OnEngineState(engine, st));
        _engine = engine;
        SetUiState(EngineState.Starting);
        try
        {
            await engine.StartAsync();
        }
        catch (Exception ex)
        {
            _engine = null;
            SetUiState(EngineState.Idle);
            if (_closingAfterStop) { _closingAfterStop = false; Close(); return; }
            Error(L.F("Impossibile avviare il test:\n\n{0}", ex.Message));
            return;
        }
        _lastSessionFolder = engine.SessionFolder;
        _lastReport = null;
        _data.StartLocal = engine.StartLocal;
        PushHistory(cfg);
        RefreshView();
        Native.KeepAwake(true);
        _timer.Start();
        TxtStatus.Text = L.F("Sessione: {0}", engine.SessionFolder);
        SetUiState(EngineState.Running);
        // Chiusura richiesta mentre il test era ancora in fase di avvio
        if (_closingAfterStop) engine.Stop(L.T("Chiusura del programma"));
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        if (_engine?.State != EngineState.Running) return;
        if (MessageBox.Show(this, L.T("Arrestare il test in corso?\nI dati raccolti restano salvati e il report viene generato."), L.T("Arresto"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _engine.Stop();
    }

    private void OnEngineState(LoadEngine engine, EngineState st)
    {
        if (engine != _engine) return;
        SetUiState(st);
        if (st is EngineState.Finished or EngineState.Failed && engine.SessionFolder.Length > 0)
        {
            OnTick();               // ultimo aggiornamento con i dati finali
            _timer.Stop();
            Native.KeepAwake(false);
            _lastReport = engine.ReportPath;
            SetUiState(st);
            if (_closingAfterStop) { Dispatcher.BeginInvoke(Close); return; }
            var snap = engine.Stats?.Snapshot;
            TxtStatus.Text = L.F("Test terminato. Sessione: {0}", engine.SessionFolder);
            if (snap != null && IsActive == false) FlashTitle();
        }
    }

    private void SetUiState(EngineState st)
    {
        bool running = st is EngineState.Starting or EngineState.Running or EngineState.Stopping;
        BtnStart.IsEnabled = !running;
        BtnStop.IsEnabled = st == EngineState.Running;
        BtnProbe.IsEnabled = !running;
        BtnLoadProfile.IsEnabled = !running;
        BtnOpenSession.IsEnabled = !running;
        ConfigPanel.IsEnabled = !running;
        BtnSnapshotReport.IsEnabled = st == EngineState.Running;
        BtnOpenFolder.IsEnabled = _lastSessionFolder != null || (_engine?.SessionFolder.Length ?? 0) > 0;
        BtnOpenReport.IsEnabled = _lastReport != null && File.Exists(_lastReport);
        TxtState.Text = st switch
        {
            EngineState.Starting => L.T("Avvio in corso…"),
            EngineState.Running => L.T("● TEST IN CORSO"),
            EngineState.Stopping => L.T("Arresto in corso…"),
            EngineState.Finished => L.T("Test terminato"),
            EngineState.Failed => L.T("Terminato con errori"),
            _ => L.T("Pronto"),
        };
        TxtState.Foreground = st switch
        {
            EngineState.Running => (Brush)FindResource("Ok"),
            EngineState.Failed => (Brush)FindResource("Danger"),
            EngineState.Stopping or EngineState.Starting => (Brush)FindResource("Warn"),
            _ => (Brush)FindResource("Muted"),
        };
    }

    private void FlashTitle()
    {
        var orig = Title;
        Title = L.T("✔ Test terminato") + " – " + orig;
        Activated += Restore;
        void Restore(object? s, EventArgs e) { Title = orig; Activated -= Restore; }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_engine is { State: EngineState.Running or EngineState.Stopping or EngineState.Starting })
        {
            if (_closingAfterStop) { e.Cancel = true; return; }
            var r = MessageBox.Show(this, L.T("C'è un test in corso. Arrestarlo, salvare i dati e chiudere?"), L.T("Chiusura"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            e.Cancel = true;
            if (r != MessageBoxResult.Yes) return;
            _closingAfterStop = true;
            TxtState.Text = L.T("Salvataggio e chiusura…");
            _engine.Stop(L.T("Chiusura del programma"));
            return;
        }
        Native.KeepAwake(false);
        base.OnClosing(e);
    }

    // =====================================================================================
    // Aggiornamento cruscotto
    // =====================================================================================

    private IEnumerable<TimeSeriesChart> Charts => [ChartQps, ChartLat, ChartFail];

    private void ResetDashboard()
    {
        _data = new ChartData();
        _loadedSnapshot = null; _loadedConfig = null;
        _nextSecond = 0; _nextMinute = 0;
        _events.Clear();
        GridFailures.ItemsSource = null; GridOutcomes.ItemsSource = null; GridTemplates.ItemsSource = null;
        _follow = true;
        RefreshView();
        foreach (var tb in new[] { KHealth, KQps, KSucc, KLat, KTotal, KFail, KTime }) tb.Text = "—";
        foreach (var tb in new[] { KHealthSub, KQpsSub, KSuccSub, KLatSub, KTotalSub, KFailSub, KTimeSub }) tb.Text = " ";
        BdHealth.Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));
    }

    private void OnTick()
    {
        // Un errore di visualizzazione non deve mai aprire una finestra al secondo né fermare il test.
        try { OnTickCore(); }
        catch (Exception ex) { App.WriteCrash(ex); }
    }

    private void OnTickCore()
    {
        var eng = _engine;
        var stats = eng?.Stats;
        if (eng == null || stats == null) return;
        _tick++;

        foreach (var s in stats.GetSeconds(_nextSecond))
        {
            _data.AddSecond(s);
            _nextSecond = s.Index + 1;
        }
        foreach (var m in stats.GetMinutes(_nextMinute))
        {
            _data.AddMinute(m);
            _nextMinute++;
        }
        RefreshView();

        var snap = stats.Snapshot;
        if (snap == null) return;
        UpdateKpis(eng.Config, snap);

        // Tabelle: solo quella visibile, ogni 2 secondi
        if (_tick % 2 == 0 || eng.State != EngineState.Running) UpdateTables(snap);

        TxtStatusRight.Text = L.F("Log query: {0:N0} righe / {1} file · connessioni {2:N0} · risposte tardive {3:N0} · coda {4:N0}",
            snap.QueryLogLines, snap.QueryLogFiles, snap.ConnectionsOpened, snap.StrayResponses, snap.PendingResults);
    }

    private void UpdateKpis(TestConfig cfg, LiveSnapshot s)
    {
        var (label, color) = s.Health switch
        {
            HealthState.Ok => (IntervalStats.HealthDisplay(HealthState.Ok), Color.FromRgb(0xDC, 0xFC, 0xE7)),
            HealthState.Degraded => (IntervalStats.HealthDisplay(HealthState.Degraded), Color.FromRgb(0xFE, 0xF3, 0xC7)),
            HealthState.Down => (IntervalStats.HealthDisplay(HealthState.Down), Color.FromRgb(0xFE, 0xE2, 0xE2)),
            _ => (IntervalStats.HealthDisplay(HealthState.NoData), Color.FromRgb(0xF3, 0xF4, 0xF6)),
        };
        KHealth.Text = label;
        BdHealth.Background = new SolidColorBrush(color);
        KHealthSub.Text = L.F("da {0} · {1} episodi", StatsCollector.FormatDuration(DateTime.Now - s.HealthSinceLocal), s.Incidents);

        var last = s.LastSecond;
        KQps.Text = last == null ? "—" : last.ResponseQps.ToString("N0", _it);
        KQpsSub.Text = L.F("inviate {0} · obiettivo {1} · picco {2:N0}", last?.SentQps.ToString("N0", _it) ?? "—",
            cfg.TargetQps > 0 ? cfg.TargetQps.ToString("N0", _it) : L.T("max"), s.PeakResponseQps);

        var w = s.Window;
        KSuccLabel.Text = L.F("Successo (ultimi {0} s)", cfg.HealthWindowSeconds);
        KSucc.Text = w == null || double.IsNaN(w.SuccessPct) ? "—" : w.SuccessPct.ToString("F2", _it) + "%";
        KSuccSub.Text = L.F("totale {0}", double.IsNaN(s.Total.SuccessPct) ? "—" : s.Total.SuccessPct.ToString("F3", _it) + "%");

        KLat.Text = w == null ? "—" : $"{Lat(w.LatP50Ms)} / {Lat(w.LatP95Ms)} / {Lat(w.LatP99Ms)}";
        KLatSub.Text = L.F("ms, finestra · totale p95 {0} p99 {1}", Lat(s.Total.LatP95Ms), Lat(s.Total.LatP99Ms));

        KTotal.Text = s.Total.Completed.ToString("N0", _it);
        KTotalSub.Text = L.F("inviate {0:N0} · AA {1}", s.Total.Sent, s.Total.Responses == 0 ? "—" : (100.0 * s.Authoritative / s.Total.Responses).ToString("F1", _it) + "%");

        long errs = s.Total.NetErrors + s.Total.OtherErrors;
        KFail.Text = $"{s.Total.Timeouts.ToString("N0", _it)} / {errs.ToString("N0", _it)}";
        KFailSub.Text = $"SERVFAIL {s.Total.ServFail:N0} · REFUSED {s.Total.Refused:N0}";

        KTime.Text = StatsCollector.FormatDuration(TimeSpan.FromSeconds(s.ElapsedSeconds));
        KTimeSub.Text = cfg.DurationSeconds > 0
            ? L.F("rimanente {0}", StatsCollector.FormatDuration(TimeSpan.FromSeconds(Math.Max(0, cfg.DurationSeconds - s.ElapsedSeconds))))
            : L.T("fino ad arresto manuale");
    }

    private string Lat(double ms) => double.IsNaN(ms) ? "—" : ms.ToString(ms < 10 ? "F1" : "F0", _it);

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == Tabs && CurrentSnapshot is { } s) UpdateTables(s);
    }

    private LiveSnapshot? CurrentSnapshot => _loadedSnapshot ?? _engine?.Stats?.Snapshot;

    /// <summary>Non aggiorna una tabella mentre l'utente vi sta selezionando o copiando righe.</summary>
    private static bool IsUserBusy(DataGrid g) => g.IsKeyboardFocusWithin || g.SelectedItems.Count > 0;

    private void UpdateTables(LiveSnapshot s)
    {
        var grid = Tabs.SelectedIndex switch { 1 => GridFailures, 2 => GridOutcomes, 3 => GridTemplates, _ => null };
        if (grid != null && grid.ItemsSource != null && IsUserBusy(grid)) return;
        switch (Tabs.SelectedIndex)
        {
            case 1:
                GridFailures.ItemsSource = s.RecentFailures.Select(r => new FailureRow(
                    r.CompletedUtc.ToLocalTime().ToString("dd/MM HH:mm:ss.fff"), r.QName, DnsTypes.ToName(r.QType), r.OutcomeLabel,
                    (r.LatencyUs / 1000.0).ToString("F1", _it), r.Detail ?? "")).ToList();
                break;
            case 2:
                long tot = Math.Max(1, s.Total.Completed);
                GridOutcomes.ItemsSource = s.Outcomes.OrderByDescending(kv => kv.Value)
                    .Select(kv => new OutcomeRow(kv.Key, kv.Value.ToString("N0", _it), (100.0 * kv.Value / tot).ToString("F3", _it) + "%")).ToList();
                break;
            case 3:
                GridTemplates.ItemsSource = s.Templates.Select(t => new TemplateRow(t.Query, t.Completed.ToString("N0", _it),
                    double.IsNaN(t.SuccessPct) ? "—" : t.SuccessPct.ToString("F2", _it) + "%", t.Timeouts.ToString("N0", _it),
                    t.Errors.ToString("N0", _it), double.IsNaN(t.AvgMs) ? "—" : t.AvgMs.ToString("F2", _it) + " ms",
                    double.IsNaN(t.AaPct) ? "—" : t.AaPct.ToString("F0", _it) + "%")).ToList();
                break;
        }
    }

    private void AddEvent(SessionEvent ev)
    {
        _events.Insert(0, new EventRow(ev.TimeLocal.ToString("dd/MM/yyyy HH:mm:ss"), ev.Level, Labels.EventCategory(ev.Category), ev.Message));
        if (_events.Count > 5000) _events.RemoveAt(_events.Count - 1);
    }

    // =====================================================================================
    // Vista dei grafici: intervallo, scorrimento, tempo reale
    // =====================================================================================

    /// <summary>Ricalcola l'intervallo visibile e aggiorna grafici, barra di scorrimento e selettori Da/A.</summary>
    private void RefreshView()
    {
        if (ChartQps == null) return;
        double end = _data.EndSeconds;
        double width, start;
        if (_viewWidth <= 0)
        {
            width = Math.Max(end, 60);
            start = 0;
        }
        else
        {
            width = _viewWidth;
            start = _follow
                ? Math.Max(0, end - width)
                : Math.Clamp(_viewStart, 0, Math.Max(0, end - MinViewSeconds));   // in storico l'intervallo scelto resta fermo
        }
        _viewStart = start;

        // Risoluzione scelta sui dati realmente visibili: a inizio test anche la vista "24 ore" usa i secondi
        bool useSeconds = Math.Min(width, end) <= SecondsResolutionMaxWindow;
        if (_lastUseSeconds != useSeconds || ChartQps.Series.Count == 0 || !ReferenceEquals(ChartQps.Series[0], useSeconds ? _data.SecQps[0] : _data.MinQps[0]))
        {
            ChartQps.Series.Clear(); ChartQps.Series.AddRange(useSeconds ? _data.SecQps : _data.MinQps);
            ChartLat.Series.Clear(); ChartLat.Series.AddRange(useSeconds ? _data.SecLat : _data.MinLat);
            ChartFail.Series.Clear(); ChartFail.Series.AddRange(useSeconds ? _data.SecFail : _data.MinFail);
            _lastUseSeconds = useSeconds;
        }
        foreach (var ch in Charts)
        {
            ch.StartLocal = _data.StartLocal;
            ch.ViewStart = start;
            ch.ViewEnd = start + width;
            ch.DataEnd = end;
            ch.Refresh();
        }

        // Barra di scorrimento: tutta la sessione, con il "pollice" grande quanto la finestra visibile
        double max = Math.Max(0, end - width);
        ScrollHistory.Minimum = 0;
        ScrollHistory.Maximum = max;
        ScrollHistory.ViewportSize = Math.Max(1, width);
        ScrollHistory.SmallChange = Math.Max(1, width / 20);
        ScrollHistory.LargeChange = Math.Max(1, width * 0.9);
        ScrollHistory.Value = Math.Clamp(start, 0, max);
        ScrollHistory.IsEnabled = _viewWidth > 0 && max > 0;

        BtnLive.IsChecked = _follow;
        BtnLive.Content = _follow ? L.T("●  Tempo reale") : L.T("⏸  Storico – torna al tempo reale");
        TxtResolution.Text = useSeconds ? L.T("risoluzione 1 secondo") : L.T("risoluzione 1 minuto (p95/p99 = valori del minuto)");
        if (_data.HasData)
        {
            var a = _data.StartLocal.AddSeconds(start);
            var b = _data.StartLocal.AddSeconds(Math.Min(start + width, Math.Max(end, start + width)));
            TxtViewInfo.Text = $"{a:dd/MM HH:mm:ss} → {b:dd/MM HH:mm:ss}  ·  {StatsCollector.FormatDuration(TimeSpan.FromSeconds(width))}";
        }
        else TxtViewInfo.Text = "";
        UpdateRangePickers(start, start + width);
    }

    /// <summary>Riporta l'intervallo visibile nei selettori Da/A, salvo che l'utente li stia modificando.</summary>
    private void UpdateRangePickers(double start, double end)
    {
        if (!_data.HasData || PanelRange.IsKeyboardFocusWithin || DpFrom.IsDropDownOpen || DpTo.IsDropDownOpen) return;
        var a = _data.StartLocal.AddSeconds(start);
        var b = _data.StartLocal.AddSeconds(end);
        if (DpFrom.SelectedDate != a.Date) DpFrom.SelectedDate = a.Date;
        if (DpTo.SelectedDate != b.Date) DpTo.SelectedDate = b.Date;
        TxtFromTime.Text = a.ToString("HH:mm:ss");
        TxtToTime.Text = b.ToString("HH:mm:ss");
    }

    /// <summary>Seleziona il pulsante d'intervallo corrispondente alla larghezza corrente, o nessuno se personalizzata.</summary>
    private void SyncRangeButtons()
    {
        _suppressRangeEvents = true;
        foreach (var rb in new[] { R5m, R30m, R2h, R24h, RAll })
        {
            double tag = double.Parse((string)rb.Tag, CultureInfo.InvariantCulture);
            rb.IsChecked = Math.Abs(tag - _viewWidth) < 0.5;
        }
        _suppressRangeEvents = false;
    }

    private void Range_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressRangeEvents || ChartQps == null) return;
        if (sender is not RadioButton { Tag: string t } || !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) return;
        double oldEnd = _viewStart + (_viewWidth > 0 ? _viewWidth : Math.Max(_data.EndSeconds, 60));
        _viewWidth = w;
        if (w <= 0) _follow = true;
        else if (!_follow) _viewStart = oldEnd - w;      // mantiene fermo l'istante finale della vista
        RefreshView();
    }

    /// <summary>Scorrimento o zoom chiesti dal mouse su un grafico: applicati a tutti e tre i grafici.</summary>
    private void OnChartViewRequested(double start, double end)
    {
        ReleaseRangeFocus();
        double total = Math.Max(_data.EndSeconds, 60);
        double reqWidth = end - start;
        double curWidth = _viewWidth > 0 ? _viewWidth : total;
        bool zoomOut = reqWidth > curWidth + 0.5;
        if (zoomOut && reqWidth >= total && curWidth <= total)
        {
            _viewWidth = 0;                 // zoom indietro oltre la durata: tutta la sessione
            _follow = true;
        }
        else
        {
            if (_viewWidth <= 0 && Math.Abs(reqWidth - curWidth) < 0.5) return;   // in "Tutto" non c'è nulla da scorrere
            double width = Math.Max(MinViewSeconds, reqWidth);
            _viewWidth = width;
            _viewStart = Math.Clamp(start, 0, Math.Max(0, _data.EndSeconds - width));
            _follow = _viewStart + width >= _data.EndSeconds - 1;
        }
        SyncRangeButtons();
        RefreshView();
    }

    private void ScrollHistory_Scroll(object sender, ScrollEventArgs e)
    {
        ReleaseRangeFocus();
        if (_viewWidth <= 0) return;
        _viewStart = e.NewValue;
        _follow = e.NewValue >= ScrollHistory.Maximum - 0.5;
        RefreshView();
    }

    private void BtnLive_Click(object sender, RoutedEventArgs e) => GoLive();

    /// <summary>Toglie il focus dai campi Da/A, così tornano a mostrare l'intervallo visibile.</summary>
    private void ReleaseRangeFocus()
    {
        if (PanelRange.IsKeyboardFocusWithin) Keyboard.Focus(BtnLive);
    }

    private void GoLive()
    {
        ReleaseRangeFocus();
        _follow = true;
        RefreshView();
    }

    private void PanelRange_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { BtnApplyRange_Click(sender, e); e.Handled = true; }
    }

    private void BtnApplyRange_Click(object sender, RoutedEventArgs e)
    {
        if (!_data.HasData) { Error(L.T("Nessun dato da visualizzare: avviare un test o aprire una sessione salvata.")); return; }
        if (!TryReadPicker(DpFrom, TxtFromTime, out var from) || !TryReadPicker(DpTo, TxtToTime, out var to))
        {
            Error(L.T("Indicare data e ora di inizio e di fine (ora nel formato hh:mm oppure hh:mm:ss)."));
            return;
        }
        if (to <= from) { Error(L.T("L'istante finale deve essere successivo a quello iniziale.")); return; }
        double a = (from - _data.StartLocal).TotalSeconds;
        double b = (to - _data.StartLocal).TotalSeconds;
        if (b <= 0 || a >= _data.EndSeconds)
        {
            Error(L.F("L'intervallo indicato è fuori dai dati disponibili ({0:dd/MM/yyyy HH:mm:ss} – {1:dd/MM/yyyy HH:mm:ss}).", _data.StartLocal, _data.StartLocal.AddSeconds(_data.EndSeconds)));
            return;
        }
        a = Math.Max(0, a);
        _viewWidth = Math.Max(MinViewSeconds, b - a);
        _viewStart = a;
        _follow = false;
        SyncRangeButtons();
        Keyboard.Focus(BtnLive);
        RefreshView();
    }

    private static bool TryReadPicker(DatePicker dp, TextBox time, out DateTime value)
    {
        value = default;
        if (dp.SelectedDate is not { } d) return false;
        string[] fmts = ["H:mm:ss", "HH:mm:ss", "H:mm", "HH:mm", "H.mm.ss", "H.mm", "%H"];
        if (!DateTime.TryParseExact(time.Text.Trim(), fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return false;
        value = d.Date + t.TimeOfDay;
        return true;
    }

    // =====================================================================================
    // Verifica, report, cartelle
    // =====================================================================================

    private async void BtnProbe_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        var cfg = ReadConfigFromUi(errors);
        errors.AddRange(cfg.Validate().Where(x => x != L.T("Indicare la cartella di output.")));
        if (errors.Count > 0) { Error(string.Join(Environment.NewLine, errors.Distinct())); return; }
        BtnProbe.IsEnabled = false;
        TxtState.Text = L.T("Verifica in corso…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var (resolved, results, warns) = await Task.Run(() => LoadEngine.ProbeAsync(cfg, 20, cts.Token));
            PushHistory(cfg);
            new ProbeWindow(cfg, resolved, results, warns) { Owner = this }.Show();
        }
        catch (Exception ex) { Error(L.F("Verifica non riuscita:\n\n{0}", ex.Message)); }
        finally { SetUiState(_engine?.State ?? EngineState.Idle); }
    }

    private async void BtnSnapshotReport_Click(object sender, RoutedEventArgs e)
    {
        var eng = _engine;
        if (eng?.Stats == null) return;
        BtnSnapshotReport.IsEnabled = false;
        try
        {
            await eng.Stats.FlushSummaryAsync(TimeSpan.FromSeconds(3));
            var path = await Task.Run(() => ReportGenerator.Generate(eng.SessionFolder, partial: true));
            OpenPath(path);
        }
        catch (Exception ex) { Error(L.F("Report parziale non generato: {0}", ex.Message)); }
        finally { BtnSnapshotReport.IsEnabled = eng.State == EngineState.Running; }
    }

    private void BtnRegenReport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = L.T("Seleziona la cartella di una sessione") };
        var baseDir = Directory.Exists(TxtOutput.Text) ? TxtOutput.Text : AppPaths.DefaultSessionsFolder;
        if (Directory.Exists(baseDir)) dlg.InitialDirectory = baseDir;
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (_engine is { State: EngineState.Running } && string.Equals(dlg.FolderName.TrimEnd('\\'), _engine.SessionFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            { BtnSnapshotReport_Click(sender, e); return; }
            var path = ReportGenerator.Generate(dlg.FolderName);
            OpenPath(path);
        }
        catch (Exception ex) { Error(L.F("Report non generato: {0}", ex.Message)); }
    }

    /// <summary>Carica nel cruscotto una sessione salvata: grafici completi, KPI riepilogativi, eventi, esiti e risultati per query.</summary>
    private async void BtnOpenSession_Click(object sender, RoutedEventArgs e)
    {
        if (_engine is { State: EngineState.Running or EngineState.Starting or EngineState.Stopping }) return;
        var dlg = new OpenFolderDialog { Title = L.T("Seleziona la cartella di una sessione da analizzare") };
        var baseDir = Directory.Exists(TxtOutput.Text) ? TxtOutput.Text : AppPaths.DefaultSessionsFolder;
        if (Directory.Exists(baseDir)) dlg.InitialDirectory = baseDir;
        if (dlg.ShowDialog(this) != true) return;
        var folder = dlg.FolderName;
        var infoPath = Path.Combine(folder, SessionFiles.SessionJson);
        if (!File.Exists(infoPath)) { Error(L.T("La cartella non contiene session.json: non è una cartella di sessione.")); return; }

        BtnOpenSession.IsEnabled = false;
        BtnStart.IsEnabled = false;          // nessun avvio di test durante il caricamento
        TxtState.Text = L.T("Caricamento sessione…");
        try
        {
            var loaded = await Task.Run(() =>
            {
                var info = System.Text.Json.JsonSerializer.Deserialize<SessionInfo>(ReadShared(infoPath), TestConfig.JsonOptions)!;
                var csv = new CsvFormat(info.CsvDialect);
                var data = ChartData.LoadFromSession(folder, info.StartLocal, csv);
                SessionSummary? summary = null;
                var sumPath = Path.Combine(folder, SessionFiles.SummaryJson);
                if (File.Exists(sumPath))
                    summary = System.Text.Json.JsonSerializer.Deserialize<SessionSummary>(ReadShared(sumPath), TestConfig.JsonOptions);
                var events = DnsLoadTester.Core.Output.EventLog.Read(Path.Combine(folder, SessionFiles.EventsCsv), csv);
                return (info, data, summary, events);
            });

            if (_engine is { State: EngineState.Starting or EngineState.Running or EngineState.Stopping } running)
            {
                SetUiState(running.State);
                return;
            }
            ResetDashboard();
            _engine = null;
            _data = loaded.data;
            _loadedConfig = loaded.info.Config;
            foreach (var ev in loaded.events.TakeLast(5000)) AddEvent(ev);
            if (loaded.summary != null)
            {
                _loadedSnapshot = SnapshotFromSummary(loaded.summary);
                ShowSavedKpis(loaded.info, loaded.summary, _loadedSnapshot);
            }
            _lastSessionFolder = folder;
            _lastReport = Directory.EnumerateFiles(folder, "report_v*.html").OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
            _viewWidth = 0; _follow = true;
            SyncRangeButtons();
            RefreshView();
            if (CurrentSnapshot is { } snap) UpdateTables(snap);
            TxtStatus.Text = L.F("Sessione aperta in sola lettura: {0}", folder);
            SetUiState(EngineState.Idle);
            TxtState.Text = L.T("Sessione salvata (analisi)");
            if (!_data.HasData) Error(L.T("La sessione non contiene ancora intervalli registrati."));
        }
        catch (Exception ex)
        {
            SetUiState(EngineState.Idle);
            Error(L.F("Impossibile aprire la sessione: {0}", ex.Message));
        }
    }

    /// <summary>Apre l'analisi del log per query; precarica la sessione corrente o l'ultima aperta, se ha un log.</summary>
    private void BtnAnalyzeLog_Click(object sender, RoutedEventArgs e)
    {
        var folder = _engine?.SessionFolder is { Length: > 0 } d ? d : _lastSessionFolder;
        new QueryLogWindow(folder) { Owner = this }.Show();
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    private static LiveSnapshot SnapshotFromSummary(SessionSummary s)
    {
        var h = s.GetHistogram();
        long O(string k) => s.Outcomes.GetValueOrDefault(k);
        var total = new IntervalStats
        {
            Seconds = s.ElapsedSeconds, Sent = s.Sent, Completed = s.Completed, Responses = s.Responses, Success = s.Success,
            NoError = O("NOERROR"), NxDomain = O("NXDOMAIN"), ServFail = O("SERVFAIL"), Refused = O("REFUSED"),
            Timeouts = s.Timeouts, NetErrors = s.NetErrors, OtherErrors = s.OtherErrors, Truncated = s.Truncated, BytesIn = s.BytesIn,
            LatMinMs = h.MinMs, LatAvgMs = h.AvgMs, LatP50Ms = h.PercentileMs(50), LatP90Ms = h.PercentileMs(90),
            LatP95Ms = h.PercentileMs(95), LatP99Ms = h.PercentileMs(99), LatMaxMs = h.MaxMs,
        };
        return new LiveSnapshot
        {
            ElapsedSeconds = s.ElapsedSeconds,
            Total = total,
            Incidents = s.HealthIncidents,
            Outcomes = s.Outcomes,
            HealthSeconds = s.HealthSeconds,
            Templates = s.Templates.ToArray(),
            Authoritative = s.Authoritative,
            StrayResponses = s.StrayResponses,
            ConnectionsOpened = s.ConnectionsOpened,
            QueryLogLines = s.QueryLogLines,
            QueryLogFiles = s.QueryLogFiles,
            PeakResponseQps = s.PeakResponseQps,
        };
    }

    /// <summary>KPI di una sessione salvata: valori complessivi (non esiste una "finestra" corrente).</summary>
    private void ShowSavedKpis(SessionInfo info, SessionSummary s, LiveSnapshot snap)
    {
        var t = snap.Total;
        long measured = s.HealthSeconds.Where(kv => kv.Key != "NESSUN DATO").Sum(kv => kv.Value);
        long ok = s.HealthSeconds.GetValueOrDefault("OK"), down = s.HealthSeconds.GetValueOrDefault("NON DISPONIBILE");
        double avail = measured == 0 ? double.NaN : 100.0 * (measured - down) / measured;
        KHealth.Text = Labels.SessionStatus(s.Status).ToUpper(_it);
        BdHealth.Background = new SolidColorBrush(down > 0 ? Color.FromRgb(0xFE, 0xE2, 0xE2)
            : s.HealthIncidents > 0 ? Color.FromRgb(0xFE, 0xF3, 0xC7) : Color.FromRgb(0xDC, 0xFC, 0xE7));
        KHealthSub.Text = L.F("disponibilità {0} · {1} episodi", double.IsNaN(avail) ? "—" : avail.ToString("F2", _it) + "%", s.HealthIncidents);
        KQps.Text = (s.Responses / Math.Max(1, s.ElapsedSeconds)).ToString("N1", _it);
        KQpsSub.Text = L.F("media sessione · obiettivo {0} · picco {1:N0}", info.Config.TargetQps > 0 ? info.Config.TargetQps.ToString("N0", _it) : L.T("max"), s.PeakResponseQps);
        KSuccLabel.Text = L.T("Successo (intera sessione)");
        KSucc.Text = double.IsNaN(t.SuccessPct) ? "—" : t.SuccessPct.ToString("F3", _it) + "%";
        KSuccSub.Text = L.F("secondi in OK {0}", measured == 0 ? "—" : (100.0 * ok / measured).ToString("F2", _it) + "%");
        KLat.Text = $"{Lat(t.LatP50Ms)} / {Lat(t.LatP95Ms)} / {Lat(t.LatP99Ms)}";
        KLatSub.Text = L.F("ms, intera sessione · max {0}", Lat(t.LatMaxMs));
        KTotal.Text = t.Completed.ToString("N0", _it);
        KTotalSub.Text = L.F("inviate {0:N0} · AA {1}", t.Sent, t.Responses == 0 ? "—" : (100.0 * s.Authoritative / t.Responses).ToString("F1", _it) + "%");
        KFail.Text = $"{t.Timeouts.ToString("N0", _it)} / {(t.NetErrors + t.OtherErrors).ToString("N0", _it)}";
        KFailSub.Text = $"SERVFAIL {t.ServFail:N0} · REFUSED {t.Refused:N0}";
        KTime.Text = StatsCollector.FormatDuration(TimeSpan.FromSeconds(s.ElapsedSeconds));
        KTimeSub.Text = $"{s.StartLocal:dd/MM/yyyy HH:mm} → {(s.EndLocal ?? s.LastUpdateLocal):dd/MM HH:mm}";
        TxtStatusRight.Text = L.F("Log query: {0:N0} righe / {1} file · connessioni {2:N0} · risposte tardive {3:N0}", s.QueryLogLines, s.QueryLogFiles, s.ConnectionsOpened, s.StrayResponses);
    }

    // =====================================================================================
    // Lingua (stesso schema di AdbSync e Pietrasanta Traceroute GUI)
    // =====================================================================================

    /// <summary>Menu 🌐 Lingua / Language: la scelta viene salvata e applicata al riavvio, che viene proposto subito.</summary>
    private void BtnLanguage_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = BtnLanguage, Placement = PlacementMode.Bottom };
        foreach (var lang in L.Available())
        {
            var mi = new MenuItem { Header = lang.Name, IsCheckable = true, IsChecked = lang.Code.Equals(L.Code, StringComparison.OrdinalIgnoreCase) };
            mi.Click += (_, _) => ChangeLanguage(lang);
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var folder = new MenuItem { Header = L.T("Apri la cartella delle traduzioni") };
        folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(L.LangDirectory);
            OpenPath(L.LangDirectory);
        };
        menu.Items.Add(folder);
        menu.IsOpen = true;
    }

    private void ChangeLanguage(LanguageInfo lang)
    {
        if (lang.Code.Equals(L.Code, StringComparison.OrdinalIgnoreCase) && _history.Language.Length > 0) return;
        _history.Language = lang.Code;
        _history.Save();
        bool busy = _engine is { State: EngineState.Running or EngineState.Starting or EngineState.Stopping };
        if (busy)
        {
            MessageBox.Show(this, L.F("La lingua «{0}» sarà usata al prossimo avvio del programma.", lang.Name), L.T("Lingua"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, L.F("La lingua «{0}» sarà usata al riavvio del programma. Riavviare ora?", lang.Name), L.T("Lingua"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
            Application.Current.Shutdown();
        }
        catch (Exception ex) { Error(L.F("Riavvio non riuscito: {0}", ex.Message)); }
    }

    /// <summary>Apre la guida HTML (incorporata nell'eseguibile, estratta accanto ai dati alla prima apertura).</summary>
    private void Help_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        try
        {
            bool en = L.Code.Equals("en", StringComparison.OrdinalIgnoreCase);
            var path = Path.Combine(AppPaths.DataFolder, en ? $"Guide_DnsLoadTester_v{AppPaths.Version}.html" : $"Guida_DnsLoadTester_v{AppPaths.Version}.html");
            using var res = typeof(MainWindow).Assembly.GetManifestResourceStream(en ? "Guide.html" : "Guida.html")
                ?? throw new FileNotFoundException(L.T("Guida non presente nell'eseguibile."));
            using var ms = new MemoryStream();
            res.CopyTo(ms);
            var bytes = ms.ToArray();
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
            {
                Directory.CreateDirectory(AppPaths.DataFolder);
                File.WriteAllBytes(path, bytes);
            }
            OpenPath(path);
        }
        catch (Exception ex) { Error(L.F("Impossibile aprire la guida: {0}", ex.Message)); }
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _engine?.SessionFolder is { Length: > 0 } d ? d : _lastSessionFolder;
        if (dir != null && Directory.Exists(dir)) OpenPath(dir);
    }

    private void BtnOpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport != null && File.Exists(_lastReport)) OpenPath(_lastReport);
    }

    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Error(L.F("Impossibile aprire {0}: {1}", path, ex.Message)); }
    }

    private void Error(string msg) => MessageBox.Show(this, msg, "DNS Load Tester", MessageBoxButton.OK, MessageBoxImage.Warning);
}

public sealed record EventRow(string Time, string Level, string Category, string Message);
public sealed record FailureRow(string Time, string Name, string Type, string Outcome, string Latency, string Detail);
public sealed record OutcomeRow(string Name, string Count, string Pct);
public sealed record TemplateRow(string Query, string Completed, string Success, string Timeouts, string Errors, string AvgLatency, string Aa);
