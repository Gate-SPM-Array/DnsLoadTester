using System.Text;
using System.Text.Json;
using DnsLoadTester.Core.Engine;
using DnsLoadTester.Core.Stats;

namespace DnsLoadTester.Core.Output;

/// <summary>Nomi dei file prodotti in ogni cartella di sessione.</summary>
public static class SessionFiles
{
    public const string SessionJson = "session.json";
    public const string SummaryJson = "summary.json";
    public const string Intervals1s = "intervals_1s.csv";
    public const string Intervals1m = "intervals_1m.csv";
    public const string EventsCsv = "events.csv";
    public const string AppLog = "app.log";
    public const string QueryLogPrefix = "queries_";
    public const string ReportPrefix = "report_v";

    public static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    public static string CreateSessionFolder(TestConfig cfg, DateTime startLocal)
    {
        string target = cfg.Protocol == DnsProtocol.DoH
            ? (Uri.TryCreate(cfg.DohUrl, UriKind.Absolute, out var u) ? u.Host : "doh")
            : cfg.Server;
        string label = string.IsNullOrWhiteSpace(cfg.SessionLabel) ? "" : "_" + cfg.SessionLabel.Trim();
        string name = $"{startLocal:yyyyMMdd_HHmmss}_{cfg.Protocol}_{target}{label}";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        name = name.Replace(' ', '_').Replace(':', '-');
        var dir = Path.Combine(cfg.OutputFolder, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Scrittura atomica (file temporaneo + rename) per non lasciare JSON corrotti in caso di crash.</summary>
    public static void WriteAllTextAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (IOException) when (attempt < 10) { Thread.Sleep(50); }   // file aperto in lettura da un altro processo
            catch (UnauthorizedAccessException) when (attempt < 10) { Thread.Sleep(50); }
        }
    }
}

/// <summary>Metadati della sessione, scritti all'avvio (session.json).</summary>
public sealed class SessionInfo
{
    public string AppVersion { get; set; } = "";
    public string Machine { get; set; } = Environment.MachineName;
    public string OsDescription { get; set; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
    public DateTime StartLocal { get; set; }
    public string ResolvedTarget { get; set; } = "";
    public CsvDialect CsvDialect { get; set; }
    public TestConfig Config { get; set; } = new();
}

/// <summary>Stato cumulativo della sessione, riscritto periodicamente (summary.json): basta per rigenerare il report.</summary>
public sealed class SessionSummary
{
    public string Status { get; set; } = "In corso";
    public DateTime StartLocal { get; set; }
    public DateTime LastUpdateLocal { get; set; }
    public DateTime? EndLocal { get; set; }
    public double ElapsedSeconds { get; set; }
    public long Sent { get; set; }
    public long Completed { get; set; }
    public long Responses { get; set; }
    public long Success { get; set; }
    public long Timeouts { get; set; }
    public long NetErrors { get; set; }
    public long OtherErrors { get; set; }
    public long Truncated { get; set; }
    public long Authoritative { get; set; }
    public long StrayResponses { get; set; }
    public long ConnectionsOpened { get; set; }
    public long BytesIn { get; set; }
    public long QueryLogLines { get; set; }
    public int QueryLogFiles { get; set; }
    public double PeakResponseQps { get; set; }
    public Dictionary<string, long> Outcomes { get; set; } = [];
    public Dictionary<string, long> ErrorDetails { get; set; } = [];
    public Dictionary<string, long> HealthSeconds { get; set; } = [];
    public int HealthIncidents { get; set; }
    public long LatCount { get; set; }
    public long LatSumUs { get; set; }
    public long LatMinUs { get; set; }
    public long LatMaxUs { get; set; }
    public Dictionary<int, long> LatHistogram { get; set; } = [];
    public List<TemplateStats> Templates { get; set; } = [];

    public LatencyHistogram GetHistogram() => LatencyHistogram.FromSparse(LatHistogram, LatMinUs, LatMaxUs, LatSumUs);

    public string ToJson() => JsonSerializer.Serialize(this, TestConfig.JsonOptions);
}
