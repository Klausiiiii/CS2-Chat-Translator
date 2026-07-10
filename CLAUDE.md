# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Purpose

Desktop tool that tails CS2's `console.log` (produced when the game is launched with `-condebug`), parses chat messages out of the log, translates them into the user's target language via free Google Translate (no API key), and renders original + translation in a standalone window. Two persistent settings: target language + path to `console.log`. Runs on Windows (WinForms or Avalonia build) and Linux (Avalonia build).

## Tech Stack

- **.NET 10** target. Core library is plain `net10.0`; WinForms app is `net10.0-windows`; Avalonia app is `net10.0`.
- **xUnit** test project at `tests/CS2ChatTranslator.Tests` — references Core, so runs as `net10.0` without a Windows dependency.
- **GTranslate** NuGet (`GoogleTranslator2`) — free, keyless, wraps the same unofficial Google endpoint as `deep-translator`.
- **Avalonia 11.2.x** for the cross-platform UI.
- Shipped as a self-contained single-file `.exe` (~51 MB for WinForms, similar for Avalonia) via `dotnet publish`.

## Project layout

```
src/
  CS2ChatTranslator.Core/         # net10.0 — Services + Models (shared)
  CS2ChatTranslator/              # net10.0-windows — WinForms UI (original)
  CS2ChatTranslator.Avalonia/     # net10.0 — Avalonia UI (Windows + Linux)
tests/
  CS2ChatTranslator.Tests/        # net10.0 — xUnit, references Core
```

Both UI projects reference `CS2ChatTranslator.Core`. Don't duplicate service logic between them — put cross-cutting code in Core. UI projects should only contain view/form code and the platform-specific dialog plumbing.

## Common commands

```bash
dotnet build                                              # build the whole solution
dotnet test                                               # run all tests (xUnit)
dotnet test --filter "FullyQualifiedName~ChatLineParserTests.Parses_GermanTeamChat_WithCallout"
dotnet run --project src/CS2ChatTranslator                # WinForms app (Windows only)
dotnet run --project src/CS2ChatTranslator.Avalonia       # Avalonia app (Windows or Linux)
```

Publish distributable binaries:

```bash
# Windows WinForms
dotnet publish src/CS2ChatTranslator/CS2ChatTranslator.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o out/win-winforms/

# Linux Avalonia
dotnet publish src/CS2ChatTranslator.Avalonia/CS2ChatTranslator.Avalonia.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o out/linux/
```

The previous run's `.exe` holds a file lock while running — if `dotnet publish` fails with a file-in-use error on Windows, check `tasklist //FI "IMAGENAME eq CS2ChatTranslator.exe"` and kill the process before republishing.

## Architecture

Pipeline, top to bottom:

```
console.log  →  ConsoleLogTailer  →  ChatLineParser  →  TranslationService  →  MainForm / MainWindow
```

All services below live in `CS2ChatTranslator.Core`.

- **`Services/ConsoleLogTailer.cs`** — opens the file with `FileShare.ReadWrite | FileShare.Delete` (mandatory, otherwise CS2 can't keep writing), polls every 500 ms, tracks byte offset, buffers partial lines, handles truncation (CS2 restart shrinks the file). Emits `LineRead` events. Both UIs construct it with `startFromBeginning: false` so only live chat is parsed — historical content in a large `console.log` would otherwise trigger one Google Translate API call per past line at startup. The constructor's `false` default also matches what tests expect. **Decoding is stateful**: each read goes through a shared `Encoding.UTF8.GetDecoder()`, not `Encoding.UTF8.GetString`, so a multibyte codepoint split across the 1 MiB read cap (or a mid-character flush) decodes correctly instead of becoming `U+FFFD` — call `_decoder.Reset()` anywhere the byte stream jumps discontinuously (seek in `OpenAndSeek`, truncation branch). `_partialLine` is **capped at 64 KiB**: a newline-free stream is discarded (and resyncs on the next `\n`) rather than growing unbounded into an O(N²) concat. `Dispose` tears the timer down with `_timer.Dispose(WaitHandle)` and waits for any in-flight tick before disposing the stream (no `ObjectDisposedException`, no reopen-after-dispose handle leak); `_disposed` is `volatile` and guards `TryReopen` and the tick's catch block. Optionally seeds the feed on start: constructed with `seedTailBytes` > 0 (both UIs pass `DefaultSeedTailBytes` = 1 MiB), it reads a bounded tail window, drops the sliced first line, and fires a one-shot `SeedRead(IReadOnlyList<string>)` batch of the window's complete lines before live tailing — leaving `_lastPosition`/`_partialLine` positioned so live `LineRead` continues without gap or duplication. The UI caps that batch to the last 25 chat messages via `ChatSeedSelector.LastMessages` and fires their translations staggered (200 ms) to avoid a startup burst on the keyless endpoint.
- **`Services/ChatLineParser.cs`** — this file carries non-obvious knowledge. Read it before touching chat-format logic:
  - CS2's client log format depends on the **game language**. German client emits `[ALLE]` (all-chat), `[AT]` (CT team, "Anti-Terror"), `[TOT]` (dead suffix). English emits `[ALL]`/`[CT]`/`[T]`/`[DEAD]`. Supported tokens are in `TypeMap` — **add new localizations there**, don't loosen the regex.
  - Each chat line is prefixed with `MM/DD HH:MM:SS ` timestamp. The regex treats it as optional so hand-pasted lines without timestamps still parse (useful in tests).
  - Player names contain a **U+200E bidi mark** (`‎`) after the name — strip before any comparison.
  - Team-chat callouts appear as `Player‎﹫<location>:` using **U+FE6B SMALL COMMERCIAL AT** (not a normal `@`). The parser extracts `Callout` into its own field; both `﹫` and regular `@` are accepted.
  - The `[TOT]`/`[DEAD]` marker is a **suffix after the player name**, not a prefix on the line (previous iteration got this wrong).
  - Non-chat system lines (`[RenderSystem]`, `[Client]`, `[Filesystem]`, …) are rejected by the whitelist. Widening the whitelist accidentally re-enables thousands of false positives.
  - `ChatLineParserTests.ExtractsOnlyChatLines_FromRealLogExcerpt` contains a real CS2 log slice — use it as the canonical regression test when changing the parser.
- **`Services/TranslationService.cs`** — wraps GTranslate's `GoogleTranslator2` behind an internal `RawTranslate` seam (the parameterless ctor wraps GTranslate; an `internal` ctor injects a fake + fast retry for tests). Auto-detects source, retries once on failure, returns `(Text, Failed, Skipped, SourceLanguage)`. `SourceLanguage` is the ISO 639-1 code Google detected (e.g. `"ru"`, `"de"`); the reply pipeline uses it to translate the user's reply back into the sender's language. Caches successful results keyed by `(text, targetLanguage)` in a bounded (1024 entries, FIFO eviction) `ConcurrentDictionary` so repeated phrases like `gg`/`ez`/callouts skip the network entirely; bounding uses a private `Interlocked` counter, **not** `ConcurrentDictionary.Count` (which locks every bucket). `Skipped=true` is set when the detected source language matches the target, and the returned `Text` is the original input. Three more guards keep the keyless endpoint from being hammered: **single-flight** (`_inflight` map coalesces concurrent identical misses onto one round-trip — a round-end burst of 10 `gg`s makes one call, not ten; the shared task ignores per-caller `CancellationToken` so one caller can't cancel it for everyone), a **30 s bounded negative cache** (a persistently failing/throttled phrase is not re-attempted on every reappearance), and a **256-char length guard** (oversized parsed lines return the original unchanged without a network call). `TranslateAsync` returns a `Task` (not `async`) so cache/guard hits complete synchronously.
- **`Services/ChatInjectionService.cs`** — writes a `cs2_translator_reply.cfg` next to CS2. The cfg path is derived from `ConsoleLogPath`: `<parent-of-console.log>/cfg/cs2_translator_reply.cfg`. `WriteSayCommand(cfgPath, message, ChatType)` emits a single `say "…"` or `say_team "…"` line in UTF-8 without BOM. Escaping: `\` → `\\`, `"` → `”` (U+201D — **not** `\"`: the Source/Source-2 console does **not** honor backslash-escaped quotes, so a literal `"` always closes the `say` argument and the tail would re-tokenize as console commands separated by `;`; substituting a non-breakout glyph is the only safe option), all control characters / newlines / tabs collapse to a single space, then the message is trimmed (so multi-line user input becomes a single chat message). The UI relies on the user having set a CS2 keybind like `bind F8 "exec cs2_translator_reply"`; this service never touches game memory or input — it only writes a cfg file the game itself executes. Tests cover escaping, file overwriting, UTF-8 encoding, and directory auto-creation. When adding chat-localization escapes, keep them inside `EscapeForSay` — the file format is contractual with CS2's `say` parser.
- **`Services/ConfigStore.cs`** — JSON round-trip at `%APPDATA%\CS2ChatTranslator\config.json` (Windows) / `~/.config/CS2ChatTranslator/config.json` (Linux) with camelCase properties, atomic write (unique temp file + `File.Move`, retried on transient sharing violations). Returns defaults (`en`, empty path) only when the file is **missing or genuinely corrupt** (`JsonException`); a transient `IOException` (AV/indexer/another instance mid-move) is retried briefly rather than mistaken for "missing" and silently resetting valid settings. `Load`/`Save` have `internal` path-overloaded variants (via `InternalsVisibleTo`) for hermetic tests. `Environment.SpecialFolder.ApplicationData` maps to both correctly — don't hardcode platform paths.
- **`Services/SteamPaths.cs`** — `FindExistingCsgoDirectory()` probes common Steam library locations per platform. Windows checks `Program Files`/`Program Files (x86)` + common secondary drives. Linux checks `~/.local/share/Steam`, `~/.steam/steam`, `~/.steam/root`, and the Flatpak path (`~/.var/app/com.valvesoftware.Steam/data/Steam`). **Add new platform roots here**, not in the UI layer.

### WinForms UI (`src/CS2ChatTranslator/`)

- **`UI/MainForm.cs`** + `MainForm.Designer.cs` — dark `RichTextBox` chat feed. Rendering is **incremental**, not full-rebuild: `AppendMessage` appends a single message's runs and stores its `[Start, TranslationStart, End)` character range in `_ranges`. When the translation arrives, `UpdateMessageTranslation` does a targeted `Select`/`SelectedText` replace inside that one range and shifts the indices of all later messages by the length delta (`ShiftRangesAfter`). Trimming (when `_messages.Count > MaxMessages`) cuts the prefix off the RichTextBox and rebases the remaining indices in `TrimOldest`. Mutations are bracketed with `WM_SETREDRAW` off/on so the user never sees a partial paint. Three `Font` instances (`_fontRegular`/`_fontBold`/`_fontItalic`) are cached at `Load` time — do **not** allocate a fresh `new Font(...)` per run, that's a multi-thousand-allocation hot path. StatusStrip shows live counters (`📖 lines / 💬 chat / 🌐 translated`) which are the go-to diagnostic when "nothing shows up."
- Reply panel: bottom `Panel _replyPanel` (label / TextBox / Send button / hint). Clicking a message in the feed calls `GetCharIndexFromPosition` and looks the position up in `_ranges` to identify the target `ChatMessage`. `OnSendReply` translates the user's text into `_replyTarget.SourceLanguage` (which `TranslationService` filled in when the original message was first translated) and writes the cfg via `ChatInjectionService`. First successful write shows a one-time onboarding `MessageBox` explaining the `bind F8 "exec cs2_translator_reply"` step.
- **`UI/SettingsForm.cs`** — hand-built WinForms dialog. Language `ComboBox` is populated with `Items.AddRange` (not `DataSource`); the `DataSource` path leaves `Items.Count == 0` until a handle is created, which makes `SelectedIndex = 0` throw. Don't switch back to `DataSource`.

### Avalonia UI (`src/CS2ChatTranslator.Avalonia/`)

- **`Views/MainWindow.axaml[.cs]`** — DockPanel with Menu, ScrollViewer + `SelectableTextBlock` for the feed, a reply panel, and a bottom status bar. The feed uses `Inlines` with `Run` elements (Avalonia's equivalent to RichTextBox colored runs). Rendering is **incremental** like WinForms: `AppendMessage` adds the `Run` list to the `Inlines` collection and stores `(All, TranslationRun, CharStart, CharEnd)` per message in `_inlines`. Translation completion mutates the stored `TranslationRun.Text/Foreground/FontStyle` in place — Avalonia re-flows the affected line only, no full rebuild. The pending → failed path inserts an extra `(Übersetzung fehlgeschlagen)` `Run` and tracks it in `TranslationFailedSuffix` so it can be removed if the same message later succeeds. A static `IBrush` palette (`BrushAll`, `BrushCT`, …) is shared across all messages — do **not** `new SolidColorBrush(...)` per run; that's the second worst allocation hot path. `Dispatcher.UIThread.Post` is the BeginInvoke equivalent. **The 500-message cap and the old 50 ms `DispatcherTimer` coalescing timer are gone**; incremental updates are cheap enough that batching adds latency without saving work.
- Reply panel: bottom `Border` wrapping a `Grid` with `ReplyTargetLabel`/`ReplyInput`/`ReplySendBtn`/`ReplyHintLabel`. Click-to-target uses `e.Source as Run` first (works when the user clicks directly on a run), with a fallback via `SelectableTextBlock.SelectionStart` mapped against per-message `CharStart/CharEnd`. The CharStart/CharEnd values must be kept in sync by `ShiftCharsAfter` (translation length changes) and `TrimOldest` (prefix removed) — if you add another mutation path, update them there too.
- **`Views/SettingsWindow.axaml[.cs]`** — ComboBox + readonly TextBox + Browse button. File picking goes through `StorageProvider.OpenFilePickerAsync` (async, unlike WinForms' `OpenFileDialog`). Dialog returns `AppConfig?` via `Close(result)` / `ShowDialog<AppConfig?>()`. Warning popups are inline `Window` + `TextBlock` (Avalonia has no built-in `MessageBox`).
- **`App.axaml`** — Fluent theme, dark variant requested globally. Don't switch to Simple theme; the dark look breaks.
- Namespaces: the project folder is `CS2ChatTranslator.Avalonia`, but the C# root namespace is `CS2ChatTranslator` to avoid colliding with the `Avalonia` NuGet namespace. If you see `Avalonia.X` ambiguity, qualify with `global::Avalonia.X`.

## Threading model

- Tailer runs on `System.Threading.Timer` callbacks (ThreadPool).
- All UI mutation goes through the UI thread: WinForms uses `BeginInvoke`, Avalonia uses `Dispatcher.UIThread.Post`. State (`_messages`, counters) is mutated only on the UI thread, so no locks.
- Translation is fire-and-forget `async void HandleNewMessage` — the message is appended to the feed immediately as `[Übersetze…]`, and the translation text replaces the placeholder when the task completes via the per-message inline/range tracking. Out-of-order completions are handled because each message owns its own slot; nothing depends on global ordering of completion.

## Platform notes for this repo

- Dev shell is **Git Bash on Windows**. Use forward slashes; `/dev/null` not `NUL`. Windows paths in JSON need `\\` escaping.
- The WinForms test project targeting changed — tests now target `net10.0` and reference `CS2ChatTranslator.Core`, **not** the Windows-only WinForms project. Don't add a reference back to the WinForms project or you'll re-introduce the Windows constraint on tests.
- The nullable `components` field in `MainForm.Designer.cs` requires `#nullable enable` at the top — Designer files are treated as generated code and don't inherit the project-level context.
- Avalonia pulls a known-vulnerable `Tmds.DBus.Protocol 0.20.0` as a transitive dep (NU1903 warning). It's only hit on Linux at runtime and Avalonia upstream will bump it; we don't pin it here. If you add a Linux release pipeline, consider an explicit `PackageReference` to a fixed version.
- Distributing binaries: SmartScreen/Defender routinely flag unsigned single-file .NET binaries. This is expected; code signing is out of scope. `.pdb` is not needed by end users.

## Git

- Default branch is `main` on GitHub (`Klausiiiii/CS2-Chat-Translator`). Local `master` was renamed to `main`.
- `.gitignore` covers `bin/ obj/ out/ *.user .vs/ .idea/` — keep build artifacts out of commits.
