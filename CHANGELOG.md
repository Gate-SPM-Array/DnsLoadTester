# Changelog

Regola: ogni versione consegnata ha un numero diverso; anche una modifica minima del codice produce una nuova versione con la sua voce qui sotto.

## v1.2 — 2026-10-17

Prima pubblicazione su GitHub.

### Novità
- **Interfaccia in italiano e inglese.** Pulsante **🌐 Lingua / Language**; la scelta viene salvata (`Language` in `DnsLoadTester.history.json`) e si applica al riavvio, che il programma propone subito (durante un test viene solo memorizzata).
  - Al primo avvio la lingua segue quella di Windows: italiano se Windows è in italiano, altrimenti inglese.
  - Stesso schema di AdbSync e Pietrasanta Traceroute GUI: il testo italiano nel codice è la chiave, l'inglese è in `lang\en.json`. Chiunque può correggere una traduzione o aggiungere una lingua con un file `lang\<codice>.json`; ciò che manca resta in italiano e una traduzione che altera i segnaposto `{0}` viene ignorata.
  - `en.json` è anche incorporato nell'eseguibile, che da solo resta autosufficiente; il file in `lang\` accanto all'exe, se presente, ha la precedenza. *🌐 Lingua → Apri la cartella delle traduzioni*.
  - Tradotti anche messaggi, eventi, report HTML e guida (F1 apre la guida nella lingua dell'interfaccia). Restano invariati, per non rompere chi li elabora, le intestazioni e i valori dei CSV (colonna `health`) e gli stati salvati in `summary.json`.
  - Numeri e date seguono la lingua scelta (it-IT / en-GB).
- **Analisi grafica del log per query** (*Analizza log query…*): carica uno o più `queries_NNNN.csv` o una cartella di sessione e mostra:
  - grafico a dispersione di **ogni singola query** (tempo × latenza, colore per esito), disegnato per pixel: milioni di punti in un attimo, in ogni pixel prevale l'esito più grave;
  - filtri per nome, tipo ed esito; NXDOMAIN valido sì/no; latenze in scala logaritmica;
  - throughput, latenza e fallimenti ricalcolati sulle query filtrate con **risoluzione adattiva** all'intervallo visibile (circa 1.200 punti, da una settimana a pochi secondi), percentili esatti per intervallo;
  - tabelle per esito e per nome (nomi `{rnd}` raggruppati), elenco delle query dell'intervallo visibile, **esportazione della selezione** in CSV (rileggibile dalla stessa finestra);
  - lettura in background annullabile, righe incomplete ignorate e contate, conferma oltre 2 GB.
- Versione mostrata come `X.Y` quando la patch è 0 (regola comune agli altri progetti).

### Altro
- Repository: README in inglese e italiano, licenza MIT, `THIRD-PARTY-NOTICES.txt`, `CLAUDE.md`.
- Test: progetto xunit `tests/DnsLoadTester.Tests` (sostituisce il self-test a riga di comando), 48 test: motore contro un server DNS finto (UDP con interruzione simulata, TCP con chiusure, DoT, DoH GET/POST, arresto manuale, report), analisi del log (lettura nei due formati, filtri, aggregati, per nome, esportazione e rilettura), formato DNS, localizzazione (ogni testo ha la sua traduzione, segnaposto identici, nessuna voce orfana, nessun testo XAML senza traduzione), coerenza del numero di versione.

## v1.1.0 — 2026-10-16

### Novità
- Cronologia degli ultimi 30 server, URL DoH e nomi TLS (campi con elenco a discesa; menu «⋯» per rimuovere voci o svuotare).
- Sezione Query: pulsanti **Carica…** e **Salva…** (nome proposto con versione `_v1.0`, `_v1.1`, …).
- Grafici con **storico completo navigabile**: barra di scorrimento, rotella, Ctrl+rotella per lo zoom, trascinamento, doppio clic per il tempo reale; intervallo **Da / A** con data e ora; pulsante **Tempo reale**. Linea dello zero rialzata e area senza dati evidenziata.
- **Apri sessione…**: carica nel cruscotto una sessione salvata per l'analisi a posteriori.
- Guida HTML integrata (F1) con il razionale tecnico di ogni elemento.

### Corretto
- Ora non valida nei campi Da/A (es. «14» o «abc»): messaggio guidato invece dell'errore generico.
- Il completamento automatico dei campi con cronologia poteva sostituire il server digitato con uno simile già in elenco.

## v1.0.1 — 2026-10-16

### Novità
- Decodifica degli **Extended DNS Error** (RFC 8914) nella colonna Dettaglio e nel report.
- Suggerimenti diagnostici nella finestra *Verifica destinazione* (REFUSED con RD disattivato → resolver; REFUSED anche con RD → ACL o zone non ospitate; risposte senza AA).

## v1.0.0 — 2026-10-15

Prima versione.
- Protocolli UDP, TCP, DoT, DoH (GET/POST, HTTP/1.1, 2, 3), DoQ; carico a ritmo fisso con rampa, worker, timeout, durata o senza limite.
- Cruscotto in tempo reale: stato del server con isteresi, indicatori, grafici, eventi, ultimi fallimenti, esiti, risultati per query.
- Registrazione completa: log per query con rotazione, aggregati al secondo e al minuto, eventi, `session.json`, `summary.json`; report HTML rigenerabile anche da sessioni interrotte.
- Programma portable per Windows x64 (eseguibile singolo con `msquic.dll`).
