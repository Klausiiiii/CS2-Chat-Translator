# Design: Start-Seed der letzten Chat-Nachrichten

**Datum:** 2026-07-10
**Status:** Freigegeben (Design), Implementierung ausstehend

## Problem

Startet der Nutzer das Tool mitten im Spiel, ist die Chat-Feed leer, bis die
nächste neue Nachricht eintrifft. Der bereits gelaufene Chat der aktuellen
CS2-Session wird nicht angezeigt.

Das aktuelle Verhalten ist **bewusst** so: beide UIs konstruieren den
`ConsoleLogTailer` mit `startFromBeginning: false` (Seek ans Dateiende), damit
ein großes `console.log` nicht beim Start eine Google-Translate-Anfrage pro
historischer Zeile auslöst (Rate-Limiting / IP-Ban des keylosen Endpoints).

## Ziel

Beim Start die **letzten ~25 Chat-Nachrichten** der aktuellen Session anzeigen
und übersetzen — hart begrenzt, ohne das gesamte Log zu verarbeiten.

## Nicht-Ziele (YAGNI)

- Kein Übersetzen des kompletten historischen Logs.
- Keine neue Benutzereinstellung (N und Fenstergröße sind Konstanten).
- Kein Zusammenführen mehrerer/rotierter Logdateien (CS2 truncatet bei Neustart).
- Kein visueller Marker zwischen History und Live-Chat.

## Ansatz: begrenztes Tail-Fenster (Ansatz A)

Der Tailer liest beim Start ein festes Byte-Fenster vom Dateiende, liefert dessen
vollständige Zeilen als einen Batch, und die UI behält daraus die letzten N
geparsten Chat-Nachrichten. Doppelt begrenzt: I/O auf ein festes Fenster,
Übersetzungen auf N.

Verworfene Alternativen:
- **B (ganzes File parsen, nur letzte N übersetzen):** liest+parst bei jedem
  Start das komplette Log; unnötige I/O-/Regex-Last bei großem Log.
- **C (adaptives Fenster):** garantiert exakt N, aber mehr Komplexität als der
  Nutzen rechtfertigt.

## Komponenten

### 1. `ConsoleLogTailer` — Seed-Fenster + Batch-Event

- Neuer optionaler ctor-Parameter `long seedTailBytes = 0`. Der bestehende
  `bool startFromBeginning` bleibt unverändert (bestehende Tests bleiben grün).
- Neues Event `event EventHandler<IReadOnlyList<string>>? SeedRead`.
- In `Start()`, wenn `seedTailBytes > 0`:
  1. Länge bestimmen, seek zu `offset = max(0, length - seedTailBytes)`.
  2. Ist `offset > 0`, die angeschnittene erste Zeile bis zum ersten `\n`
     **verwerfen** (Resync) — sonst würde Zeilen-Müll emittiert.
  3. Die vollständigen Zeilen des Fensters sammeln und **als ein Batch** über
     `SeedRead` liefern (synchron in `Start()`).
  4. `_lastPosition` ans Fensterende setzen, `_partialLine`/`_decoder` in den
     korrekten Zustand für das nachfolgende Live-Tailing bringen.
  5. Timer wie gehabt starten; Live-Chat läuft unverändert über `LineRead`.
- Fenstergröße = **1 MiB**, bewusst gleich dem bestehenden 1-MiB-Read-Cap, damit
  das Fenster in genau einen Read passt und der vorhandene Decoder-/Partial-Line-
  Pfad wiederverwendet wird.
- Wird `seedTailBytes` nicht gesetzt (Default 0), ist das Verhalten identisch zu
  heute.

### 2. Reine Cap-Funktion in Core (testbar)

Eine reine Funktion (kein UI-Zustand) nimmt die Roh-Batch-Zeilen, parst sie mit
`ChatLineParser` und gibt die **letzten N** geparsten Chat-Nachrichten in
chronologischer Reihenfolge zurück. Liegt in Core, damit das Cap-Verhalten
hermetisch testbar ist, ohne UI.

### 3. UI-Orchestrierung (`MainForm.cs` + `MainWindow.axaml.cs`)

- Beide UIs konstruieren den Tailer mit dem 1-MiB-Seed-Fenster.
- Neuer Handler `HandleSeed(batch)`:
  1. Über die Cap-Funktion die letzten N Chat-Nachrichten bestimmen.
  2. Alle chronologisch wie normale Nachrichten mit `[Übersetze…]`-Placeholder
     rendern (bestehender inkrementeller Render-Pfad).
  3. Übersetzungen **gestaffelt** anstoßen (siehe unten).
- Danach unverändert: `HandleNewMessage` für Live-Chat. Kein Marker zwischen
  History und Live.

### 4. Gestaffelte Seed-Übersetzung

- Statt N Übersetzungen gleichzeitig zu starten, werden sie mit einer kleinen
  Verzögerung (Konstante, ~200 ms) nacheinander **angestoßen** (nicht awaited —
  das bestehende Out-of-Order-Completion-Modell bleibt: jede Nachricht besitzt
  ihren eigenen Slot).
- Umsetzung über eine async-Schleife, die den UI-Thread nicht blockiert
  (`await Task.Delay` zwischen den Starts). Senkt das Burst-/Ban-Risiko beim
  keylosen Endpoint.

## Parameter (Konstanten)

| Name | Wert | Begründung |
|------|------|-----------|
| Seed-Fenster | 1 MiB | passt in einen Read, gleich dem bestehenden Read-Cap |
| N (Seed-Nachrichten) | 25 | begrenzter, praktisch ausreichender Chat-Kontext |
| Staffel-Delay | ~200 ms | billig, senkt Burst-Risiko; ~5 s bis alle 25 gestartet |

## Bekanntes Manko (akzeptiert)

Enthält das 1-MiB-Fenster wegen viel System-Log-Noise weniger als N Chat-Zeilen,
werden entsprechend weniger angezeigt. In der Praxis irrelevant; die Konstante
kann bei Bedarf erhöht werden.

## Threading

- `SeedRead` wird synchron in `Start()` auf dem aufrufenden Thread gefeuert (vor
  dem Timer-Start). Die UI marshallt das Rendern wie gewohnt auf den UI-Thread
  (`BeginInvoke` / `Dispatcher.UIThread.Post`).
- Die gestaffelte Übersetzungsschleife läuft async ohne UI-Thread-Blockade.

## Testplan

- **Tailer-Seed:** Fenster-Seek bei `length > window` und `length < window`;
  Resync der angeschnittenen ersten Zeile bei `offset > 0`; korrekter
  Batch-Inhalt; `_lastPosition` danach korrekt, sodass anschließend angehängter
  Live-Text lückenlos über `LineRead` erscheint (kein Verlust, keine Dopplung).
- **Cap-Funktion:** aus gemischtem Roh-Batch (Chat + System-Zeilen) genau die
  letzten N Chat-Nachrichten in korrekter Reihenfolge; weniger als N vorhanden →
  alle; keine Chat-Zeilen → leer.
- **Regression:** bestehende `ConsoleLogTailerTests` bleiben grün
  (Default-Verhalten unverändert).
