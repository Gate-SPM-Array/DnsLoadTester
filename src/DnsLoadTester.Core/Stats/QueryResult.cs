using DnsLoadTester.Core.Dns;

namespace DnsLoadTester.Core.Stats;

/// <summary>Esito di una singola query.</summary>
public enum QueryOutcome : byte
{
    /// <summary>Risposta DNS valida ricevuta (vedi Rcode).</summary>
    Response,
    Timeout,
    NetworkError,
    TlsError,
    HttpError,
    /// <summary>Risposta malformata, ID o domanda non corrispondenti, connessione chiusa.</summary>
    ProtocolError,
}

public readonly record struct QueryResult(
    long Seq,
    DateTime CompletedUtc,
    long CompletedTs,
    int WorkerId,
    int TemplateIndex,
    string QName,
    ushort QType,
    QueryOutcome Outcome,
    int Rcode,
    int LatencyUs,
    int ResponseBytes,
    int Answers,
    bool Authoritative,
    bool Truncated,
    string? Detail)
{
    public bool IsResponse => Outcome == QueryOutcome.Response;

    public string OutcomeLabel => Outcome switch
    {
        QueryOutcome.Response => DnsRcodes.ToName(Rcode),
        QueryOutcome.Timeout => "TIMEOUT",
        QueryOutcome.NetworkError => "NET_ERROR",
        QueryOutcome.TlsError => "TLS_ERROR",
        QueryOutcome.HttpError => "HTTP_ERROR",
        _ => "PROTOCOL_ERROR",
    };

    /// <summary>Considerata "risposta utile": NOERROR (e, se configurato, NXDOMAIN).</summary>
    public bool IsSuccess(bool nxIsSuccess) =>
        Outcome == QueryOutcome.Response && (Rcode == DnsRcodes.NoError || (nxIsSuccess && Rcode == DnsRcodes.NxDomain));
}
