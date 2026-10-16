using System.Globalization;
using System.Text;
using DnsLoadTester.Core.Output;

namespace DnsLoadTester.Core.Analysis;

/// <summary>Classe di esito usata per colori, filtri e statistiche dell'analisi a posteriori.</summary>
public enum QueryClass : byte { NoError, NxDomain, ServFail, Refused, OtherRcode, Timeout, Error }

/// <summary>
/// Contenuto di uno o più file <c>queries_NNNN.csv</c> in memoria, in forma compatta a colonne
/// (circa 30 byte per query: 5 milioni di query ≈ 150 MB). Le righe sono ordinate per istante di conclusione.
/// Nomi, esiti e dettagli sono memorizzati una sola volta (tabelle di stringhe) e referenziati per indice.
/// </summary>
public sealed class QueryLog
{
    /// <summary>Istante a cui corrisponde T = 0 (il secondo intero della prima query).</summary>
    public DateTime StartLocal { get; private set; }
    public int Count { get; private set; }
    /// <summary>Durata coperta dai dati, in secondi.</summary>
    public double SpanSeconds { get; private set; }

    public double[] T = [];        // secondi da StartLocal
    public float[] LatMs = [];
    public int[] NameId = [];
    public ushort[] QType = [];
    public byte[] OutcomeId = [];
    public QueryClass[] Class = [];
    public short[] Rcode = [];
    public byte[] Flags = [];      // bit 0 = AA, bit 1 = TC
    public int[] DetailId = [];    // -1 = nessun dettaglio
    public short[] Worker = [];
    public ushort[] RespBytes = [];
    public ushort[] Answers = [];

    public List<string> Names { get; } = [];
    public List<string> Outcomes { get; } = [];
    public List<string> Details { get; } = [];
    public List<string> Files { get; } = [];
    public long SkippedLines { get; private set; }

    public bool IsAuthoritative(int i) => (Flags[i] & 1) != 0;
    public bool IsTruncated(int i) => (Flags[i] & 2) != 0;
    public bool HasResponse(int i) => Class[i] <= QueryClass.OtherRcode;
    public DateTime TimeOf(int i) => StartLocal.AddSeconds(T[i]);

    public bool IsSuccess(int i, bool nxIsSuccess) =>
        Class[i] == QueryClass.NoError || (nxIsSuccess && Class[i] == QueryClass.NxDomain);

    public static QueryClass Classify(string outcome) => outcome switch
    {
        "NOERROR" => QueryClass.NoError,
        "NXDOMAIN" => QueryClass.NxDomain,
        "SERVFAIL" => QueryClass.ServFail,
        "REFUSED" => QueryClass.Refused,
        "TIMEOUT" => QueryClass.Timeout,
        "NET_ERROR" or "TLS_ERROR" or "HTTP_ERROR" or "PROTOCOL_ERROR" => QueryClass.Error,
        _ => QueryClass.OtherRcode,
    };

    /// <summary>
    /// Carica i file indicati (stesso formato prodotto dal programma, entrambi i dialetti CSV, riconosciuti dall'intestazione).
    /// Le righe incomplete o non riconosciute sono contate in <see cref="SkippedLines"/> e saltate.
    /// </summary>
    public static QueryLog Load(IEnumerable<string> files, IProgress<(long Lines, string File)>? progress = null, CancellationToken ct = default)
    {
        var log = new QueryLog();
        var b = new Builder();
        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            log.Files.Add(file);
            log.SkippedLines += ReadFile(file, b, progress, ct);
        }
        log.Fill(b);
        return log;
    }

    /// <summary>Tutti i <c>queries_*.csv</c> di una cartella di sessione, nell'ordine di rotazione.</summary>
    public static List<string> SessionFiles(string folder) =>
        Directory.EnumerateFiles(folder, SessionFilesPattern).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public const string SessionFilesPattern = "queries_*.csv";

    private sealed class Builder
    {
        public readonly List<DateTime> Time = [];
        public readonly List<float> Lat = [];
        public readonly List<int> Name = [];
        public readonly List<ushort> Type = [];
        public readonly List<byte> Outcome = [];
        public readonly List<short> Rcode = [];
        public readonly List<byte> Flags = [];
        public readonly List<int> Detail = [];
        public readonly List<short> Worker = [];
        public readonly List<ushort> Bytes = [];
        public readonly List<ushort> Answers = [];
        public readonly Dictionary<string, int> NameIx = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> OutcomeIx = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> DetailIx = new(StringComparer.Ordinal);
        public readonly List<string> Names = [], Outcomes = [], Details = [];

        public int Intern(Dictionary<string, int> ix, List<string> list, string s)
        {
            if (ix.TryGetValue(s, out int i)) return i;
            i = list.Count;
            list.Add(s);
            ix[s] = i;
            return i;
        }
    }

    private static long ReadFile(string path, Builder b, IProgress<(long, string)>? progress, CancellationToken ct)
    {
        long skipped = 0, lines = 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var r = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1 << 16);
        var header = r.ReadLine();
        if (header == null) return 0;
        var dialect = header.Contains(';') ? CsvDialect.ExcelItalian : CsvDialect.Standard;
        var csv = new CsvFormat(dialect);
        var cols = csv.Split(header);
        int Ix(string n) => Array.IndexOf(cols, n);
        int iTs = Ix("timestamp"), iWorker = Ix("worker"), iName = Ix("qname"), iType = Ix("qtype"), iOut = Ix("outcome"),
            iRcode = Ix("rcode"), iLat = Ix("latency_ms"), iAa = Ix("aa"), iTc = Ix("tc"), iDet = Ix("detail"),
            iBytes = Ix("resp_bytes"), iAns = Ix("answers");
        if (iTs < 0 || iName < 0 || iOut < 0 || iLat < 0)
            throw new InvalidDataException(L.F("{0}: non è un log per query di DNS Load Tester (intestazione non riconosciuta).", Path.GetFileName(path)));

        string? line;
        while ((line = r.ReadLine()) != null)
        {
            if (++lines % 100_000 == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((b.Time.Count, Path.GetFileName(path)));
            }
            if (line.Length == 0) continue;
            var f = csv.Split(line);
            if (f.Length < cols.Length) { skipped++; continue; }    // riga troncata (crash durante la scrittura)
            if (!DateTime.TryParseExact(f[iTs], CsvFormat.TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts)) { skipped++; continue; }
            double lat = csv.ParseNumSafe(f[iLat]);
            if (double.IsNaN(lat)) { skipped++; continue; }
            b.Time.Add(ts);
            b.Lat.Add((float)lat);
            b.Name.Add(b.Intern(b.NameIx, b.Names, f[iName]));
            b.Type.Add(iType >= 0 && Dns.DnsTypes.TryParse(f[iType], out var qt) ? qt : (ushort)0);
            b.Outcome.Add((byte)Math.Min(255, b.Intern(b.OutcomeIx, b.Outcomes, f[iOut])));
            b.Rcode.Add(iRcode >= 0 && short.TryParse(f[iRcode], out var rc) ? rc : (short)-1);
            byte flags = 0;
            if (iAa >= 0 && f[iAa] == "1") flags |= 1;
            if (iTc >= 0 && f[iTc] == "1") flags |= 2;
            b.Flags.Add(flags);
            b.Detail.Add(iDet >= 0 && f[iDet].Length > 0 ? b.Intern(b.DetailIx, b.Details, f[iDet]) : -1);
            b.Worker.Add(iWorker >= 0 && short.TryParse(f[iWorker], out var w) ? w : (short)-1);
            b.Bytes.Add(iBytes >= 0 && ushort.TryParse(f[iBytes], out var by) ? by : (ushort)0);
            b.Answers.Add(iAns >= 0 && ushort.TryParse(f[iAns], out var an) ? an : (ushort)0);
        }
        progress?.Report((b.Time.Count, Path.GetFileName(path)));
        return skipped;
    }

    private void Fill(Builder b)
    {
        int n = b.Time.Count;
        Count = n;
        Names.AddRange(b.Names); Outcomes.AddRange(b.Outcomes); Details.AddRange(b.Details);
        if (n == 0) { StartLocal = DateTime.Now; return; }

        // Ordine per istante di conclusione (i file di una sessione lo sono già: l'ordinamento serve solo se si mescolano file)
        var order = Enumerable.Range(0, n).ToArray();
        bool sorted = true;
        for (int i = 1; i < n && sorted; i++) if (b.Time[i] < b.Time[i - 1]) sorted = false;
        if (!sorted) Array.Sort(b.Time.Select(t => t.Ticks).ToArray(), order);

        var first = b.Time[order[0]];
        StartLocal = new DateTime(first.Year, first.Month, first.Day, first.Hour, first.Minute, first.Second, first.Kind);
        T = new double[n]; LatMs = new float[n]; NameId = new int[n]; QType = new ushort[n]; OutcomeId = new byte[n];
        Class = new QueryClass[n]; Rcode = new short[n]; Flags = new byte[n]; DetailId = new int[n]; Worker = new short[n];
        RespBytes = new ushort[n]; Answers = new ushort[n];
        var classOf = b.Outcomes.Select(Classify).ToArray();
        for (int k = 0; k < n; k++)
        {
            int i = order[k];
            T[k] = (b.Time[i] - StartLocal).TotalSeconds;
            LatMs[k] = b.Lat[i];
            NameId[k] = b.Name[i];
            QType[k] = b.Type[i];
            OutcomeId[k] = b.Outcome[i];
            Class[k] = classOf[b.Outcome[i]];
            Rcode[k] = b.Rcode[i];
            Flags[k] = b.Flags[i];
            DetailId[k] = b.Detail[i];
            Worker[k] = b.Worker[i];
            RespBytes[k] = b.Bytes[i];
            Answers[k] = b.Answers[i];
        }
        SpanSeconds = Math.Max(1, T[n - 1]);
    }
}
