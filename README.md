# DNS Load Tester v1.2

**English** · [Italiano](README.it.md)

A Windows tool to measure, over minutes, hours or days, how well a **DNS server** answers queries — designed first of all for **external authoritative servers**, and usable with recursive resolvers too. It sends a controlled stream of queries over **UDP, TCP, DoT, DoH or DoQ**, shows the server's behaviour live, and **records everything on disk**: every single query, per-second and per-minute aggregates, events and a final HTML report.

Inspired by [dnspyre](https://github.com/Tantalor93/dnspyre) (command-line DNS benchmark), rewritten from scratch in C# / .NET 10 with a WPF interface aimed at long-running availability tests.

> **Language:** the program speaks **English** and **Italian**. Choose it with the **🌐 Language** button; it applies at the next start (the program offers to restart). On first start it follows the Windows language. Translations are plain JSON files anyone can fix or add: see [Translations](#9-translations).

> **Use it responsibly.** Run tests only against your own servers or with the owner's written authorisation. Even moderate load, kept up for hours against someone else's infrastructure, can look like an attack.

---

## 1. Requirements

- Windows 10/11 x64 or Windows Server 2016 and later. Nothing to install: the exe is self-contained (no .NET runtime needed).
- DoQ and DoH over HTTP/3 need **Windows 11 or Windows Server 2022** or later, and `msquic.dll` next to the exe (included).

## 2. Quick start

1. Extract `DnsLoadTester_v1.2_win-x64.zip` anywhere (a USB stick works) and run `DnsLoadTester.exe`.
2. **Target:** protocol, server address and port. The server, DoH URL and TLS name fields remember the last 30 values.
3. **Queries:** one per line, `name TYPE` (for example `example.com SOA`). `{rnd}` is replaced by a random label on every query (checks NXDOMAIN and bypasses caches). **Load…** / **Save…** read and write query lists.
   Example lists are in `examples/`: one for an authoritative server, one for testing a recursive resolver (RD flag on).
4. **Check target** sends each query once and shows outcome, latency and the AA flag (authoritative answer) — always do it before a long test.
5. **Load and duration:** target QPS, workers, timeout, ramp-up, duration (or no limit). Then **Start test**.

Rule of thumb for workers: to keep the target rate even when the server stops answering (every query waits for the timeout) you need at least `QPS × timeout` workers, e.g. 200 qps × 2 s = 400. The program warns you when you have fewer.

## 3. What you see

- **Server state** (OK / DEGRADED / UNAVAILABLE), assessed every second over a sliding window with configurable thresholds and hysteresis; every change is logged as an event with its duration and reason.
- **KPIs:** responses/s, success %, latency p50/p95/p99, totals, timeouts and errors, elapsed time.
- **Charts** (throughput, latency, failures) that keep the **whole session** in memory and can be scrolled and zoomed at any time: range buttons (5 min … All), **From / To** date-time pickers, scroll bar, mouse wheel, Ctrl+wheel zoom, drag, double-click back to **Real time**.
- **Tabs:** events, latest failures, outcomes, per-query results.
- **Check target** window with automatic hints (e.g. "REFUSED with RD=0: this is a resolver, turn on the RD flag") and decoded **Extended DNS Errors** (RFC 8914).

## 4. Analysing results afterwards

- **Open session…** loads any saved session (even an interrupted one, even from another PC) into the dashboard, fully navigable.
- **Analyse query log…** opens the per-query analysis window on the raw `queries_NNNN.csv` files:
  - a **scatter chart of every single query** (time × latency, coloured by outcome; millions of points rendered as a pixel raster, the worst outcome wins in each pixel so isolated problems stay visible);
  - filters by **name**, **type** and **outcome**;
  - throughput, latency and failure charts recomputed on the filtered queries with **adaptive resolution** (always about 1,200 points for the visible range, from a whole week down to a few seconds);
  - tables by outcome and **by name** (random `{rnd}` names grouped), list of the individual queries in the visible range, **export of the selection** to CSV.
- **Report from session…** regenerates the HTML report of any session folder (`report_v1.1.html`, `v1.2`, … — never overwritten).

## 5. Files of a session

One folder per session, `Sessioni\YYYYMMDD_hhmmss_PROTOCOL_server[_label]`:

| File | Content |
|---|---|
| `queries_NNNN.csv` | one line per query (time, name, type, outcome, RCODE, latency, bytes, answers, AA/TC, detail), rotated by size |
| `intervals_1s.csv`, `intervals_1m.csv` | per-second and per-minute aggregates (QPS, outcomes, latency percentiles, server state) |
| `events.csv`, `app.log` | session events and server state changes |
| `session.json` | full configuration, resolved address, program version |
| `summary.json` | totals and the complete latency histogram (rewritten every 30 s) |
| `report_vX.Y.html` | final report with charts; prints to PDF from the browser |

Files are flushed to disk every second: a crash or power cut does not lose what was already measured. CSV format is selectable: *Excel Italian* (`;` and decimal comma) or *Standard* (`,` and decimal point). CSV headers and the values of the `health` column are never translated.

## 6. Portable program

Everything stays in the program folder:

```
DnsLoadTester\
  DnsLoadTester.exe, msquic.dll
  lang\en.json                     English translation (also built into the exe; this file wins)
  DnsLoadTester.settings.json      last configuration used
  DnsLoadTester.history.json       last 30 servers / DoH URLs / TLS names, chosen language
  Guide_DnsLoadTester_v1.2.html    this guide in English (F1); Guida_… in Italian
  Sessioni\                        one folder per test session
```

If the folder is read-only (for example under `C:\Program Files`), settings go to `%LOCALAPPDATA%\DnsLoadTester` and sessions to `Documents\DnsLoadTester\Sessioni`. During a test the PC is kept awake.

## 7. How it works

- **Open-loop load:** query *n* is sent at time *n* / QPS on an absolute grid (with an optional linear ramp-up). If sending falls behind by more than 1 s the grid is realigned instead of catching up with a burst, which would distort the measurement exactly when the server is struggling (the *coordinated omission* problem). Falling behind is visible as "Sent/s" below the target.
- **Workers:** each has its own channel (connected UDP socket, TCP/TLS connection, QUIC connection, shared HTTP client for DoH) and one query in flight.
- **Validation:** a response counts only if it has the QR bit, the same ID and the same question as the query (case-insensitive), like a resolver's anti-spoofing check. Late UDP responses are discarded and counted.
- **Latency** is measured on the client from sending to complete reception (including the handshake when a connection must be opened) and collected in log-linear histograms (≤ 3 % error); percentiles of seconds, minutes, window and session are exact over each set, never averages of percentiles.
- **Statistics** run on a dedicated thread, separate from the senders.

The built-in guide (**? Help**, F1) describes every element of the interface with its technical rationale.

## 8. Building from source

```
dotnet build -c Release            # .NET 10 SDK; on Linux EnableWindowsTargeting is already set for the WPF app
dotnet test -c Release             # about 2 minutes: includes live tests against a fake DNS server (UDP, TCP, DoT, DoH)
dotnet publish src/DnsLoadTester.App -c Release -r win-x64 -o publish -p:DebugType=none
```

Layout: `src/DnsLoadTester.Core` (engine, transports, statistics, files, report, query-log analysis — no UI), `src/DnsLoadTester.App` (WPF), `tests/DnsLoadTester.Tests` (xunit), `lang/` (translations).

## 9. Translations

Every text of the program goes through `lang\<code>.json` files. To add a language:

1. Copy `lang\en.json` to `lang\<code>.json` (e.g. `de.json`) next to the exe (🌐 Language → *Open the translations folder*).
2. Set `"code"`, `"language"` (name shown in the menu), `"culture"` (number/date format, e.g. `de-DE`) and `"translators"`.
3. Translate the **values** in `"strings"`. Keys are the original Italian texts and must not be changed. Keep placeholders such as `{0}` or `{1:N0}` exactly: a translation that changes them is ignored.
4. Restart and pick the language. Untranslated texts stay in Italian.

Pull requests with new languages are welcome.

## 10. License

Released under the [MIT](LICENSE) license. Third-party components: see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## 11. Credits

Developed by [Gate-SPM-Array](https://github.com/Gate-SPM-Array) with [Claude](https://claude.com) (Anthropic), which wrote the code based on the maintainer's requirements, design decisions and testing on real DNS servers.

DNS Load Tester is an independent project, not affiliated with dnspyre, which is its functional reference.
