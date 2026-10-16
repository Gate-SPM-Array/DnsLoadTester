namespace DnsLoadTester.Core.Dns;

/// <summary>Tabella dei tipi di record DNS più comuni (RFC 1035 e successive).</summary>
public static class DnsTypes
{
    private static readonly Dictionary<string, ushort> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = 1, ["NS"] = 2, ["CNAME"] = 5, ["SOA"] = 6, ["PTR"] = 12, ["HINFO"] = 13,
        ["MX"] = 15, ["TXT"] = 16, ["RP"] = 17, ["AFSDB"] = 18, ["AAAA"] = 28, ["LOC"] = 29,
        ["SRV"] = 33, ["NAPTR"] = 35, ["KX"] = 36, ["CERT"] = 37, ["DNAME"] = 39, ["OPT"] = 41,
        ["DS"] = 43, ["SSHFP"] = 44, ["IPSECKEY"] = 45, ["RRSIG"] = 46, ["NSEC"] = 47,
        ["DNSKEY"] = 48, ["DHCID"] = 49, ["NSEC3"] = 50, ["NSEC3PARAM"] = 51, ["TLSA"] = 52,
        ["SMIMEA"] = 53, ["CDS"] = 59, ["CDNSKEY"] = 60, ["OPENPGPKEY"] = 61, ["CSYNC"] = 62,
        ["ZONEMD"] = 63, ["SVCB"] = 64, ["HTTPS"] = 65, ["SPF"] = 99, ["AXFR"] = 252,
        ["ANY"] = 255, ["URI"] = 256, ["CAA"] = 257,
    };

    private static readonly Dictionary<ushort, string> ById = ByName.ToDictionary(kv => kv.Value, kv => kv.Key.ToUpperInvariant());

    /// <summary>Tipi proposti nell'interfaccia.</summary>
    public static readonly string[] Common = ["A", "AAAA", "NS", "SOA", "MX", "TXT", "CNAME", "SRV", "PTR", "CAA", "DS", "DNSKEY", "HTTPS", "SVCB", "TLSA", "ANY"];

    public static bool TryParse(string text, out ushort type)
    {
        text = text.Trim();
        if (ByName.TryGetValue(text, out type)) return true;
        if (text.StartsWith("TYPE", StringComparison.OrdinalIgnoreCase) && ushort.TryParse(text.AsSpan(4), out type)) return true;
        return ushort.TryParse(text, out type) && type > 0;
    }

    public static string ToName(ushort type) => ById.TryGetValue(type, out var n) ? n : "TYPE" + type;
}

/// <summary>Codici di risposta DNS (RCODE, inclusi quelli estesi EDNS).</summary>
public static class DnsRcodes
{
    public const int NoError = 0, FormErr = 1, ServFail = 2, NxDomain = 3, NotImp = 4, Refused = 5,
        YxDomain = 6, YxRrSet = 7, NxRrSet = 8, NotAuth = 9, NotZone = 10, BadVers = 16;

    public static string ToName(int rcode) => rcode switch
    {
        NoError => "NOERROR", FormErr => "FORMERR", ServFail => "SERVFAIL", NxDomain => "NXDOMAIN",
        NotImp => "NOTIMP", Refused => "REFUSED", YxDomain => "YXDOMAIN", YxRrSet => "YXRRSET",
        NxRrSet => "NXRRSET", NotAuth => "NOTAUTH", NotZone => "NOTZONE", BadVers => "BADVERS",
        _ => "RCODE" + rcode,
    };
}
