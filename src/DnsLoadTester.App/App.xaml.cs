using System.IO;
using System.Windows;
using System.Windows.Threading;
using DnsLoadTester.Core;

namespace DnsLoadTester.App;

public partial class App : Application
{
    /// <summary>Messaggi del caricamento della lingua (file mancante, traduzioni scartate), mostrati nella barra di stato.</summary>
    public static IReadOnlyList<string> LanguageMessages { get; private set; } = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        // La lingua va caricata prima di creare qualsiasi finestra: i testi XAML si traducono al caricamento.
        LanguageMessages = L.Load(UiHistory.Current.Language);
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => WriteCrash(ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => { WriteCrash(ev.Exception); ev.SetObserved(); };
        base.OnStartup(e);
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = WriteCrash(e.Exception);
        MessageBox.Show(L.F("Si è verificato un errore imprevisto nell'interfaccia:\n\n{0}\n\nIl test eventualmente in corso continua e i dati restano salvati su disco.\nDettagli in: {1}",
                            e.Exception.Message, path),
                        "DNS Load Tester", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
        if (MainWindow is not { IsLoaded: true }) Shutdown(1);   // errore all'avvio: non lasciare un processo invisibile
    }

    public static string WriteCrash(Exception? ex)
    {
        try
        {
            var dir = AppPaths.DataFolder;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "crash.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\n\n");
            return path;
        }
        catch { return L.T("(non scrivibile)"); }
    }
}
