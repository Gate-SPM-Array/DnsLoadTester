using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using DnsLoadTester.Core.Output;

namespace DnsLoadTester.App;

/// <summary>
/// Cronologia degli ultimi valori usati (server, URL DoH, nome TLS), salvata in DnsLoadTester.history.json.
/// Un valore entra in cronologia solo quando viene usato davvero (avvio test o verifica destinazione).
/// </summary>
public sealed class UiHistory
{
    public const int MaxEntries = 30;

    public ObservableCollection<string> Servers { get; set; } = [];
    public ObservableCollection<string> DohUrls { get; set; } = [];
    public ObservableCollection<string> TlsNames { get; set; } = [];

    /// <summary>Lingua scelta dal menu 🌐 ("" = automatica: italiano se Windows è in italiano, altrimenti inglese).</summary>
    public string Language { get; set; } = "";

    private static UiHistory? _current;
    /// <summary>Istanza unica condivisa (letta una volta all'avvio).</summary>
    public static UiHistory Current => _current ??= Load();

    public static string FilePath => Path.Combine(AppPaths.DataFolder, "DnsLoadTester.history.json");

    public static UiHistory Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiHistory>(File.ReadAllText(FilePath)) ?? new UiHistory();
        }
        catch { /* cronologia illeggibile: si riparte vuota */ }
        return new UiHistory();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            SessionFiles.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non critico */ }
    }

    /// <summary>Porta il valore in cima alla lista (senza duplicati, maiuscole/minuscole ignorate) e limita a 30 voci.</summary>
    public static void Push(ObservableCollection<string> list, string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return;
        for (int i = list.Count - 1; i >= 0; i--)
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) list.RemoveAt(i);
        list.Insert(0, value);
        while (list.Count > MaxEntries) list.RemoveAt(list.Count - 1);
    }

    public static bool Remove(ObservableCollection<string> list, string? value)
    {
        value = value?.Trim();
        bool removed = false;
        for (int i = list.Count - 1; i >= 0; i--)
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) { list.RemoveAt(i); removed = true; }
        return removed;
    }
}
