using System.Buffers.Binary;
using System.Text;

namespace DnsLoadTester.Core.Dns;

/// <summary>Opzioni con cui viene costruita ogni query.</summary>
public readonly record struct QueryOptions(bool RecursionDesired, bool UseEdns, ushort EdnsBufferSize, bool DnssecOk, bool CheckingDisabled);

/// <summary>Informazioni estratte da una risposta DNS.</summary>
public struct DnsResponseInfo
{
    public ushort Id;
    public bool IsResponse;
    public bool Authoritative;
    public bool Truncated;
    public bool RecursionAvailable;
    public int Rcode;               // include i bit estesi da OPT
    public int Opcode;
    public ushort QdCount, AnCount, NsCount, ArCount;
    public bool QuestionMatches;
    public bool HasOpt;
    /// <summary>Extended DNS Error (RFC 8914): codice, -1 se assente.</summary>
    public int EdeCode;
    public string? EdeText;
}

/// <summary>Costruzione e parsing minimale (ma completo per lo scopo) di messaggi DNS in formato wire.</summary>
public static class DnsWire
{
    public const int MaxNameLength = 255;

    /// <summary>Scrive la query in <paramref name="buffer"/>; restituisce la lunghezza.</summary>
    public static int BuildQuery(Span<byte> buffer, ushort id, string qname, ushort qtype, in QueryOptions opt)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer, id);
        ushort flags = 0;
        if (opt.RecursionDesired) flags |= 0x0100;
        if (opt.CheckingDisabled) flags |= 0x0010;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], flags);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[4..], 1);  // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(buffer[6..], 0);  // ANCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(buffer[8..], 0);  // NSCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(buffer[10..], (ushort)(opt.UseEdns ? 1 : 0));
        int pos = 12;
        pos += WriteName(buffer[pos..], qname);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[pos..], qtype); pos += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[pos..], 1); pos += 2; // IN
        if (opt.UseEdns)
        {
            buffer[pos++] = 0;                                                   // root
            BinaryPrimitives.WriteUInt16BigEndian(buffer[pos..], 41); pos += 2;  // OPT
            BinaryPrimitives.WriteUInt16BigEndian(buffer[pos..], opt.EdnsBufferSize); pos += 2;
            buffer[pos++] = 0;  // extended rcode
            buffer[pos++] = 0;  // version
            BinaryPrimitives.WriteUInt16BigEndian(buffer[pos..], (ushort)(opt.DnssecOk ? 0x8000 : 0)); pos += 2;
            BinaryPrimitives.WriteUInt16BigEndian(buffer[pos..], 0); pos += 2;   // rdlen
        }
        return pos;
    }

    /// <summary>Scrive un nome in formato etichette. Lancia ArgumentException se non valido.</summary>
    public static int WriteName(Span<byte> buffer, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == ".") { buffer[0] = 0; return 1; }
        if (name.EndsWith('.')) name = name[..^1];
        int pos = 0;
        foreach (var label in name.Split('.'))
        {
            int len = Encoding.ASCII.GetByteCount(label);
            if (len == 0 || len > 63) throw new ArgumentException(L.F("Etichetta non valida nel nome '{0}'.", name));
            buffer[pos++] = (byte)len;
            pos += Encoding.ASCII.GetBytes(label, buffer[pos..]);
        }
        buffer[pos++] = 0;
        if (pos > MaxNameLength) throw new ArgumentException(L.F("Nome troppo lungo: '{0}'.", name));
        return pos;
    }

    /// <summary>Normalizza e valida un nome (eventuale conversione IDN in punycode).</summary>
    public static string NormalizeName(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == ".") return ".";
        bool trailing = name.EndsWith('.');
        var core = trailing ? name[..^1] : name;
        if (core.Any(c => c > 127))
            core = new System.Globalization.IdnMapping().GetAscii(core);
        Span<byte> tmp = stackalloc byte[512];
        WriteName(tmp, core.Replace("{rnd}", "abcdefgh", StringComparison.OrdinalIgnoreCase));
        return core;
    }

    /// <summary>Analizza una risposta e verifica che la domanda corrisponda alla query inviata.</summary>
    public static bool TryParseResponse(ReadOnlySpan<byte> msg, ReadOnlySpan<byte> query, out DnsResponseInfo info)
    {
        info = default;
        info.EdeCode = -1;
        if (msg.Length < 12) return false;
        info.Id = BinaryPrimitives.ReadUInt16BigEndian(msg);
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(msg[2..]);
        info.IsResponse = (flags & 0x8000) != 0;
        info.Opcode = (flags >> 11) & 0xF;
        info.Authoritative = (flags & 0x0400) != 0;
        info.Truncated = (flags & 0x0200) != 0;
        info.RecursionAvailable = (flags & 0x0080) != 0;
        info.Rcode = flags & 0xF;
        info.QdCount = BinaryPrimitives.ReadUInt16BigEndian(msg[4..]);
        info.AnCount = BinaryPrimitives.ReadUInt16BigEndian(msg[6..]);
        info.NsCount = BinaryPrimitives.ReadUInt16BigEndian(msg[8..]);
        info.ArCount = BinaryPrimitives.ReadUInt16BigEndian(msg[10..]);

        int pos = 12;
        // Confronto della sezione question con quella della query (case-insensitive, RFC 4343).
        if (info.QdCount >= 1 && query.Length > 12)
        {
            int qStart = pos;
            if (!SkipName(msg, ref pos) || pos + 4 > msg.Length) return info.Truncated; // risposte troncate possono essere incomplete
            int qEnd = pos + 4;
            var qSection = query[12..];
            int qLen = QuestionLength(qSection);
            info.QuestionMatches = qLen > 0 && qEnd - qStart == qLen &&
                                   EqualsIgnoreCaseAscii(msg[qStart..qEnd], qSection[..qLen]);
            pos = qEnd;
            for (int i = 1; i < info.QdCount; i++)
            {
                if (!SkipName(msg, ref pos) || pos + 4 > msg.Length) return true;
                pos += 4;
            }
        }
        else if (info.QdCount == 0)
        {
            info.QuestionMatches = false;
        }

        // Scorre answer/authority e cerca OPT in additional per l'RCODE esteso.
        int rrTotal = info.AnCount + info.NsCount;
        for (int i = 0; i < rrTotal; i++)
            if (!SkipRecord(msg, ref pos, out _, out _, out _)) return true;
        for (int i = 0; i < info.ArCount; i++)
        {
            int rrStart = pos;
            if (!SkipRecord(msg, ref pos, out ushort type, out _, out uint ttl)) return true;
            if (type == 41)
            {
                ParseOptOptions(msg[rrStart..pos], ref info);
                info.HasOpt = true;
                int ext = (int)(ttl >> 24) & 0xFF;
                info.Rcode |= ext << 4;
            }
        }
        return true;
    }

    /// <summary>Estrae l'opzione EDE (codice 15) dall'RDATA del record OPT.</summary>
    private static void ParseOptOptions(ReadOnlySpan<byte> rr, ref DnsResponseInfo info)
    {
        int p = 0;
        if (!SkipName(rr, ref p) || p + 10 > rr.Length) return;
        int rdlen = BinaryPrimitives.ReadUInt16BigEndian(rr[(p + 8)..]);
        var rdata = rr.Slice(p + 10, Math.Min(rdlen, rr.Length - p - 10));
        int q = 0;
        while (q + 4 <= rdata.Length)
        {
            ushort code = BinaryPrimitives.ReadUInt16BigEndian(rdata[q..]);
            ushort len = BinaryPrimitives.ReadUInt16BigEndian(rdata[(q + 2)..]);
            if (q + 4 + len > rdata.Length) return;
            if (code == 15 && len >= 2)
            {
                info.EdeCode = BinaryPrimitives.ReadUInt16BigEndian(rdata[(q + 4)..]);
                if (len > 2)
                    info.EdeText = Encoding.UTF8.GetString(rdata.Slice(q + 6, len - 2)).TrimEnd('\0').Trim();
                return;
            }
            q += 4 + len;
        }
    }

    /// <summary>Nomi dei codici Extended DNS Error (registro IANA).</summary>
    public static string EdeName(int code) => code switch
    {
        0 => "Other Error", 1 => "Unsupported DNSKEY Algorithm", 2 => "Unsupported DS Digest Type", 3 => "Stale Answer",
        4 => "Forged Answer", 5 => "DNSSEC Indeterminate", 6 => "DNSSEC Bogus", 7 => "Signature Expired",
        8 => "Signature Not Yet Valid", 9 => "DNSKEY Missing", 10 => "RRSIGs Missing", 11 => "No Zone Key Bit Set",
        12 => "NSEC Missing", 13 => "Cached Error", 14 => "Not Ready", 15 => "Blocked", 16 => "Censored",
        17 => "Filtered", 18 => "Prohibited", 19 => "Stale NXDOMAIN Answer", 20 => "Not Authoritative",
        21 => "Not Supported", 22 => "No Reachable Authority", 23 => "Network Error", 24 => "Invalid Data",
        25 => "Signature Expired before Valid", 26 => "Too Early", 27 => "Unsupported NSEC3 Iterations Value",
        28 => "Unable to conform to policy", 29 => "Synthesized", 30 => "Invalid Query Type",
        _ => "Code " + code,
    };

    private static int QuestionLength(ReadOnlySpan<byte> q)
    {
        int p = 0;
        while (p < q.Length)
        {
            int l = q[p];
            if (l == 0) { p++; return p + 4 <= q.Length ? p + 4 : -1; }
            p += l + 1;
        }
        return -1;
    }

    private static bool EqualsIgnoreCaseAscii(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            byte x = a[i], y = b[i];
            if (x == y) continue;
            if (x >= 'A' && x <= 'Z') x += 32;
            if (y >= 'A' && y <= 'Z') y += 32;
            if (x != y) return false;
        }
        return true;
    }

    private static bool SkipName(ReadOnlySpan<byte> msg, ref int pos)
    {
        int guard = 0;
        while (pos < msg.Length && guard++ < 128)
        {
            byte l = msg[pos];
            if (l == 0) { pos++; return true; }
            if ((l & 0xC0) == 0xC0) { pos += 2; return pos <= msg.Length; }
            if ((l & 0xC0) != 0) return false;
            pos += l + 1;
        }
        return false;
    }

    private static bool SkipRecord(ReadOnlySpan<byte> msg, ref int pos, out ushort type, out ushort cls, out uint ttl)
    {
        type = 0; cls = 0; ttl = 0;
        if (!SkipName(msg, ref pos) || pos + 10 > msg.Length) return false;
        type = BinaryPrimitives.ReadUInt16BigEndian(msg[pos..]);
        cls = BinaryPrimitives.ReadUInt16BigEndian(msg[(pos + 2)..]);
        ttl = BinaryPrimitives.ReadUInt32BigEndian(msg[(pos + 4)..]);
        ushort rdlen = BinaryPrimitives.ReadUInt16BigEndian(msg[(pos + 8)..]);
        pos += 10 + rdlen;
        return pos <= msg.Length;
    }
}
