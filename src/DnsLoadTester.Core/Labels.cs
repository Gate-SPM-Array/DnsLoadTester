namespace DnsLoadTester.Core;

/// <summary>
/// Traduzione per la visualizzazione dei valori che il programma salva nei file e rilegge (stato della sessione,
/// categoria degli eventi): nei file restano in italiano come chiavi stabili, a video compaiono nella lingua attiva.
/// </summary>
public static class Labels
{
    public static string SessionStatus(string status) => status switch
    {
        "In corso" => L.T("In corso"),
        "Completato" => L.T("Completato"),
        "Interrotto" => L.T("Interrotto"),
        "Errore" => L.T("Errore"),
        "Ricostruito dagli intervalli" => L.T("Ricostruito dagli intervalli"),
        _ => status,
    };

    public static string EventCategory(string category) => category switch
    {
        "Sessione" => L.T("Sessione"),
        "Salute" => L.T("Stato"),
        "Query" => L.T("Query"),
        "Motore" => L.T("Motore"),
        "Report" => L.T("Report"),
        "Sistema" => L.T("Sistema"),
        _ => category,
    };
}
