# DNS Load Tester v1.2

[English](README.md) · **Italiano**

Strumento per Windows che misura nel tempo (minuti, ore o giorni) la capacità di un **server DNS** di rispondere alle query: pensato prima di tutto per i **server autoritativi esterni**, si usa anche con i resolver ricorsivi. Invia un flusso controllato di query in **UDP, TCP, DoT, DoH o DoQ**, mostra in tempo reale il comportamento del server e **registra tutto su disco**: ogni singola query, aggregati al secondo e al minuto, eventi e un report HTML finale.

Ispirato a [dnspyre](https://github.com/Tantalor93/dnspyre) (benchmark DNS a riga di comando), riscritto da zero in C# / .NET 10 con interfaccia WPF orientata ai test di disponibilità di lunga durata.

> **Lingua:** il programma parla **italiano** e **inglese**. Si sceglie con il pulsante **🌐 Lingua** e si applica all'avvio successivo (il programma propone il riavvio). Al primo avvio segue la lingua di Windows. Le traduzioni sono semplici file JSON che chiunque può correggere o aggiungere: vedi [Traduzioni](#9-traduzioni).

> **Uso responsabile.** Eseguire i test solo su server propri o con autorizzazione scritta del titolare. Anche un carico moderato, protratto per ore verso un'infrastruttura di terzi, può sembrare un attacco.

---

## 1. Requisiti

- Windows 10/11 x64 oppure Windows Server 2016 o successivi. Nessuna installazione: l'exe è autosufficiente (non serve il runtime .NET).
- DoQ e DoH su HTTP/3 richiedono **Windows 11 o Windows Server 2022** o successivi e `msquic.dll` accanto all'exe (inclusa).

## 2. Avvio rapido

1. Estrarre `DnsLoadTester_v1.2_win-x64.zip` dove si preferisce (anche su chiavetta) e avviare `DnsLoadTester.exe`.
2. **Destinazione:** protocollo, indirizzo del server e porta. I campi server, URL DoH e nome TLS ricordano gli ultimi 30 valori.
3. **Query:** una per riga, `nome TIPO` (ad esempio `dominio.it SOA`). `{rnd}` è sostituito a ogni query da un'etichetta casuale (verifica NXDOMAIN e aggira le cache). **Carica…** / **Salva…** leggono e scrivono gli elenchi.
   Elenchi di esempio in `examples/`: uno per un server autoritativo, uno per provare un resolver ricorsivo (Flag RD attivo).
4. **Verifica destinazione** invia una volta ogni query e mostra esito, latenza e flag AA (risposta autoritativa): da fare sempre prima di un test lungo.
5. **Carico e durata:** QPS obiettivo, worker, timeout, rampa, durata (o senza limite). Poi **Avvia test**.

Regola pratica per i worker: per mantenere il ritmo anche quando il server smette di rispondere (ogni query attende il timeout) servono almeno `QPS × timeout` worker, ad esempio 200 qps × 2 s = 400. Il programma avvisa se sono meno.

## 3. Cosa si vede

- **Stato del server** (OK / DEGRADATO / NON DISPONIBILE), valutato ogni secondo su una finestra mobile con soglie configurabili e isteresi; ogni cambio è registrato come evento con durata e motivazione.
- **Indicatori:** risposte/s, successo %, latenze p50/p95/p99, totali, timeout ed errori, tempo trascorso.
- **Grafici** (throughput, latenza, fallimenti) che conservano **l'intera sessione** e si possono scorrere e ingrandire in qualsiasi momento: pulsanti d'intervallo (5 min … Tutto), selettori **Da / A** con data e ora, barra di scorrimento, rotella, Ctrl+rotella per lo zoom, trascinamento, doppio clic per tornare al **Tempo reale**.
- **Schede:** eventi, ultimi fallimenti, esiti, risultati per query.
- Finestra **Verifica destinazione** con suggerimenti automatici (ad esempio «REFUSED con RD=0: è un resolver, attivare il Flag RD») e decodifica degli **Extended DNS Error** (RFC 8914).

## 4. Analisi a posteriori

- **Apri sessione…** carica nel cruscotto qualsiasi sessione salvata (anche interrotta, anche di un altro PC), completamente navigabile.
- **Analizza log query…** apre la finestra di analisi sui file grezzi `queries_NNNN.csv`:
  - **grafico a dispersione di ogni singola query** (tempo × latenza, colore per esito; milioni di punti disegnati come immagine per pixel, in ogni pixel prevale l'esito più grave così i problemi isolati restano visibili);
  - filtri per **nome**, **tipo** ed **esito**;
  - grafici di throughput, latenza e fallimenti ricalcolati sulle query filtrate con **risoluzione adattiva** (sempre circa 1.200 punti per l'intervallo visibile, da una settimana intera fino a pochi secondi);
  - tabelle per esito e **per nome** (i nomi casuali `{rnd}` raggruppati), elenco delle singole query dell'intervallo visibile, **esportazione della selezione** in CSV.
- **Report da sessione…** rigenera il report HTML di qualsiasi cartella di sessione (`report_v1.1.html`, `v1.2`, … — mai sovrascritti).

## 5. File di una sessione

Una cartella per sessione, `Sessioni\AAAAMMGG_hhmmss_PROTOCOLLO_server[_etichetta]`:

| File | Contenuto |
|---|---|
| `queries_NNNN.csv` | una riga per query (orario, nome, tipo, esito, RCODE, latenza, byte, risposte, AA/TC, dettaglio), con rotazione per dimensione |
| `intervals_1s.csv`, `intervals_1m.csv` | aggregati al secondo e al minuto (QPS, esiti, percentili di latenza, stato del server) |
| `events.csv`, `app.log` | eventi della sessione e cambi di stato del server |
| `session.json` | configurazione completa, indirizzo risolto, versione del programma |
| `summary.json` | totali e istogramma completo delle latenze (riscritto ogni 30 s) |
| `report_vX.Y.html` | report finale con grafici; si stampa in PDF dal browser |

I file sono scritti su disco ogni secondo: un crash o un'interruzione di corrente non fa perdere ciò che era già stato misurato. Formato CSV selezionabile: *Excel italiano* (`;` e virgola decimale) o *Standard* (`,` e punto decimale). Le intestazioni dei CSV e i valori della colonna `health` non vengono mai tradotti.

## 6. Programma portable

Tutto resta nella cartella del programma:

```
DnsLoadTester\
  DnsLoadTester.exe, msquic.dll
  lang\en.json                     traduzione inglese (incorporata anche nell'exe; questo file ha la precedenza)
  DnsLoadTester.settings.json      ultima configurazione usata
  DnsLoadTester.history.json       ultimi 30 server / URL DoH / nomi TLS, lingua scelta
  Guida_DnsLoadTester_v1.2.html    guida in italiano (F1); Guide_… in inglese
  Sessioni\                        una cartella per ogni sessione di test
```

Se la cartella è in sola lettura (ad esempio sotto `C:\Program Files`), le impostazioni vanno in `%LOCALAPPDATA%\DnsLoadTester` e le sessioni in `Documenti\DnsLoadTester\Sessioni`. Durante un test il PC non va in sospensione.

## 7. Come funziona

- **Carico a ritmo fisso (open loop):** la query *n* parte all'istante *n* / QPS su una griglia assoluta (con rampa lineare facoltativa). Se l'invio resta indietro di oltre 1 s la griglia viene riallineata invece di recuperare con una raffica, che falserebbe la misura proprio quando il server è in difficoltà (il problema della *coordinated omission*). Il ritardo è visibile come «Inviate/s» sotto l'obiettivo.
- **Worker:** ognuno ha il proprio canale (socket UDP connesso, connessione TCP/TLS, connessione QUIC, client HTTP condiviso per DoH) e una sola query in volo.
- **Validazione:** una risposta conta solo se ha il bit QR, lo stesso ID e la stessa domanda della query (senza distinzione tra maiuscole e minuscole), come il controllo anti-contraffazione di un resolver. Le risposte UDP tardive sono scartate e contate.
- **Latenza** misurata lato client dall'invio alla ricezione completa (compreso l'handshake quando va aperta una connessione) e raccolta in istogrammi log-lineari (errore ≤ 3%); i percentili di secondi, minuti, finestra e sessione sono esatti su ciascun insieme, mai medie di percentili.
- **Statistiche** elaborate da un thread dedicato, separato da quelli di invio.

La guida integrata (**? Guida**, F1) descrive ogni elemento dell'interfaccia con il relativo razionale tecnico.

## 8. Compilazione dai sorgenti

```
dotnet build -c Release            # SDK .NET 10; su Linux EnableWindowsTargeting è già impostato per l'app WPF
dotnet test -c Release             # circa 2 minuti: comprende test dal vivo contro un server DNS finto (UDP, TCP, DoT, DoH)
dotnet publish src/DnsLoadTester.App -c Release -r win-x64 -o publish -p:DebugType=none
```

Struttura: `src/DnsLoadTester.Core` (motore, trasporti, statistiche, file, report, analisi del log — nessuna interfaccia), `src/DnsLoadTester.App` (WPF), `tests/DnsLoadTester.Tests` (xunit), `lang/` (traduzioni).

## 9. Traduzioni

Ogni testo del programma passa dai file `lang\<codice>.json`. Per aggiungere una lingua:

1. Copiare `lang\en.json` in `lang\<codice>.json` (es. `de.json`) accanto all'exe (🌐 Lingua → *Apri la cartella delle traduzioni*).
2. Impostare `"code"`, `"language"` (nome mostrato nel menu), `"culture"` (formato di numeri e date, es. `de-DE`) e `"translators"`.
3. Tradurre i **valori** di `"strings"`. Le chiavi sono i testi originali in italiano e non vanno modificate. Mantenere identici i segnaposto come `{0}` o `{1:N0}`: una traduzione che li altera viene ignorata.
4. Riavviare e scegliere la lingua. I testi non tradotti restano in italiano.

Le pull request con nuove lingue sono benvenute.

## 10. Licenza

Distribuito con licenza [MIT](LICENSE). Componenti di terze parti: vedi [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## 11. Crediti

Sviluppato da [Gate-SPM-Array](https://github.com/Gate-SPM-Array) con [Claude](https://claude.com) (Anthropic), che ha scritto il codice sulla base dei requisiti, delle scelte progettuali e dei test su server DNS reali del maintainer.

DNS Load Tester è un progetto indipendente, non affiliato a dnspyre, che ne è il riferimento funzionale.
