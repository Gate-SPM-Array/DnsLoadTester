using System.Text.Json;
using System.Text.Json.Serialization;
using DnsLoadTester.Core.Output;

namespace DnsLoadTester.Core.Engine;

public enum DnsProtocol { Udp, Tcp, DoT, DoH, DoQ }

public enum DohMethod { Post, Get }

public enum DohHttpVersion { Http11, Http2, Http3 }

public enum QueryLogMode { All, FailuresOnly, Off }

public enum QueryOrder { RoundRobin, Random }

/// <summary>Configurazione completa di una sessione di test. Serializzata in session.json e nei profili.</summary>
public sealed class TestConfig
{
    // --- Destinazione ---
    public string Server { get; set; } = "";
    public int Port { get; set; } = 53;
    public DnsProtocol Protocol { get; set; } = DnsProtocol.Udp;
    public string DohUrl { get; set; } = "https://dns.example.com/dns-query";
    public DohMethod DohMethod { get; set; } = DohMethod.Post;
    public DohHttpVersion DohHttpVersion { get; set; } = DohHttpVersion.Http2;
    public string TlsServerName { get; set; } = "";
    public bool TlsSkipValidation { get; set; }
    public bool ReuseConnections { get; set; } = true;
    /// <summary>Preferenza di famiglia IP quando Server è un hostname: 0 = qualsiasi, 4 = IPv4, 6 = IPv6.</summary>
    public int IpFamily { get; set; }

    // --- Query ---
    public List<string> Queries { get; set; } = ["example.com A"];
    public string DefaultQueryType { get; set; } = "A";
    public QueryOrder Order { get; set; } = QueryOrder.RoundRobin;
    public bool RecursionDesired { get; set; }
    public bool UseEdns { get; set; } = true;
    public int EdnsBufferSize { get; set; } = 1232;
    public bool DnssecOk { get; set; }

    // --- Carico ---
    public int Concurrency { get; set; } = 10;
    /// <summary>Query al secondo complessive; 0 = nessun limite (massimo throughput).</summary>
    public int TargetQps { get; set; } = 100;
    /// <summary>Durata in secondi; 0 = fino a stop manuale.</summary>
    public long DurationSeconds { get; set; } = 3600;
    public int TimeoutMs { get; set; } = 2000;
    /// <summary>Secondi di rampa iniziale fino al QPS obiettivo (0 = nessuna rampa).</summary>
    public int RampUpSeconds { get; set; }

    // --- Valutazione salute ---
    public int HealthWindowSeconds { get; set; } = 10;
    public double DegradedSuccessPct { get; set; } = 99.0;
    public double DownSuccessPct { get; set; } = 50.0;
    public int DegradedP95Ms { get; set; } = 500;
    public bool NxDomainIsSuccess { get; set; } = true;

    // --- Output ---
    public string OutputFolder { get; set; } = "";
    public string SessionLabel { get; set; } = "";
    public QueryLogMode QueryLog { get; set; } = QueryLogMode.All;
    public int QueryLogRotateMb { get; set; } = 200;
    public CsvDialect CsvDialect { get; set; } = CsvDialect.ExcelItalian;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public TestConfig Clone() => JsonSerializer.Deserialize<TestConfig>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static TestConfig FromJson(string json) => JsonSerializer.Deserialize<TestConfig>(json, JsonOptions) ?? new TestConfig();

    public static int DefaultPort(DnsProtocol p) => p switch
    {
        DnsProtocol.DoT or DnsProtocol.DoQ => 853,
        DnsProtocol.DoH => 443,
        _ => 53,
    };

    /// <summary>Descrizione sintetica della destinazione, usata in UI, log e report.</summary>
    public string TargetDescription => Protocol == DnsProtocol.DoH
        ? $"DoH {DohUrl} ({DohMethod.ToString().ToUpperInvariant()}, {DohHttpVersion})"
        : $"{Protocol.ToString().ToUpperInvariant()} {Server}:{Port}";

    /// <summary>
    /// Concorrenza minima per sostenere il QPS obiettivo anche quando tutte le query vanno in timeout
    /// (ogni worker ha una sola query in volo: worker ≥ QPS × timeout).
    /// </summary>
    public int ConcurrencyForSustainedQps => TargetQps <= 0 ? 0 : (int)Math.Ceiling(TargetQps * TimeoutMs / 1000.0);

    /// <summary>Avvisi non bloccanti sulla configurazione.</summary>
    public List<string> Warnings()
    {
        var w = new List<string>();
        int need = ConcurrencyForSustainedQps;
        if (TargetQps > 0 && Concurrency < need)
            w.Add(L.F("Con {0} worker e timeout {1} ms, se il server smette di rispondere il carico scende a circa {2:N0} qps. Per mantenere {3} qps anche durante un'interruzione servono almeno {4} worker.",
                  Concurrency, TimeoutMs, Concurrency * 1000.0 / TimeoutMs, TargetQps, need));
        if (TargetQps == 0)
            w.Add(L.T("QPS illimitato: il test invierà il massimo possibile. Su un server autoritativo esterno usare solo con autorizzazione esplicita."));
        if (QueryLog == QueryLogMode.All && TargetQps > 0 && DurationSeconds > 0)
        {
            double gb = TargetQps * (double)DurationSeconds * 110 / 1e9;
            if (gb > 2) w.Add(L.F("Il log per query occuperà circa {0:N1} GB. Valutare «Solo fallimenti» per test molto lunghi.", gb));
        }
        if (Protocol != DnsProtocol.Udp && TlsSkipValidation && Protocol != DnsProtocol.Tcp)
            w.Add(L.T("Verifica del certificato TLS disattivata."));
        return w;
    }

    /// <summary>Controlla la coerenza della configurazione; restituisce l'elenco degli errori.</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (Protocol == DnsProtocol.DoH)
        {
            if (!Uri.TryCreate(DohUrl, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
                errors.Add(L.T("URL DoH non valido (deve iniziare con https://)."));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Server)) errors.Add(L.T("Indicare il server DNS (IP o nome)."));
            if (Port is < 1 or > 65535) errors.Add(L.T("Porta non valida."));
        }
        if (Concurrency is < 1 or > 5000) errors.Add(L.T("Worker: da 1 a 5000."));
        if (TargetQps < 0) errors.Add(L.T("Il QPS non può essere negativo."));
        if (TimeoutMs is < 50 or > 60000) errors.Add(L.T("Timeout: da 50 a 60000 ms."));
        if (DurationSeconds < 0) errors.Add(L.T("Durata non valida."));
        if (EdnsBufferSize is < 512 or > 65535) errors.Add(L.T("Buffer EDNS: da 512 a 65535 byte."));
        if (Queries.All(string.IsNullOrWhiteSpace)) errors.Add(L.T("Inserire almeno una query."));
        if (string.IsNullOrWhiteSpace(OutputFolder)) errors.Add(L.T("Indicare la cartella di output."));
        return errors;
    }
}
