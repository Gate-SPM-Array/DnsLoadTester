using DnsLoadTester.Core.Dns;

namespace DnsLoadTester.Core.Engine;

/// <summary>Una voce dell'elenco query. Il nome può contenere il segnaposto {rnd} (etichetta casuale per query).</summary>
public sealed record QueryTemplate(int Index, string Name, ushort Type)
{
    public bool HasRandom { get; } = Name.Contains("{rnd}", StringComparison.OrdinalIgnoreCase);
    public string TypeName => DnsTypes.ToName(Type);
    public string Display => $"{Name} {TypeName}";
}

/// <summary>Insieme di query da inviare, con selezione round-robin o casuale (thread-safe).</summary>
public sealed class QuerySet
{
    private readonly QueryTemplate[] _items;
    private readonly QueryOrder _order;
    private long _counter = -1;
    private const string RndChars = "abcdefghijklmnopqrstuvwxyz0123456789";

    public IReadOnlyList<QueryTemplate> Items => _items;

    private QuerySet(QueryTemplate[] items, QueryOrder order) { _items = items; _order = order; }

    /// <summary>
    /// Analizza le righe: "nome [TIPO]" oppure "TIPO nome"; righe vuote e commenti (#, ;) ignorati.
    /// Accetta anche il formato CSV "nome,TIPO".
    /// </summary>
    public static QuerySet Parse(IEnumerable<string> lines, string defaultType, QueryOrder order, out List<string> warnings)
    {
        warnings = [];
        if (!DnsTypes.TryParse(defaultType, out ushort defType)) defType = 1;
        var list = new List<QueryTemplate>();
        int lineNo = 0;
        foreach (var raw in lines)
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            var parts = line.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
            string name; ushort type = defType;
            if (parts.Length == 1) name = parts[0];
            else if (DnsTypes.TryParse(parts[1], out var t2)) { name = parts[0]; type = t2; }
            else if (DnsTypes.TryParse(parts[0], out var t1) && !parts[0].Contains('.')) { name = parts[1]; type = t1; }
            else { warnings.Add(L.F("Riga {0}: tipo non riconosciuto in '{1}', uso {2}.", lineNo, line, DnsTypes.ToName(defType))); name = parts[0]; }

            try { name = DnsWire.NormalizeName(name); }
            catch (Exception ex) { warnings.Add(L.F("Riga {0}: '{1}' scartata ({2})", lineNo, line, ex.Message)); continue; }
            list.Add(new QueryTemplate(list.Count, name, type));
        }
        return new QuerySet([.. list], order);
    }

    public QueryTemplate Next()
    {
        long n = Interlocked.Increment(ref _counter);
        if (_order == QueryOrder.Random) return _items[Random.Shared.Next(_items.Length)];
        return _items[(int)(n % _items.Length)];
    }

    /// <summary>Restituisce il nome effettivo (sostituisce {rnd} con un'etichetta casuale di 10 caratteri).</summary>
    public static string Materialize(QueryTemplate t)
    {
        if (!t.HasRandom) return t.Name;
        return string.Create(t.Name.Length + 5, t.Name, static (span, name) =>
        {
            int idx = name.IndexOf("{rnd}", StringComparison.OrdinalIgnoreCase);
            name.AsSpan(0, idx).CopyTo(span);
            var rnd = span.Slice(idx, 10);
            for (int i = 0; i < rnd.Length; i++) rnd[i] = RndChars[Random.Shared.Next(RndChars.Length)];
            name.AsSpan(idx + 5).CopyTo(span[(idx + 10)..]);
        });
    }
}
