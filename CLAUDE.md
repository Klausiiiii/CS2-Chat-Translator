# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Purpose

Windows desktop tool that tails CS2's `console.log` (produced when the game is launched with `-condebug`), parses chat messages out of the log, translates them into the user's target language via free Google Translate (no API key), and renders original + translation in a standalone window. Two persistent settings: target language + path to `console.log`.

## Tech Stack

- **.NET 10 / WinForms** (`net10.0-windows`). Single target framework — do not introduce cross-plat abstractions; the whole thing is Windows-only by design.
- **xUnit** test project at `tests/CS2ChatTranslator.Tests`.
- **GTranslate** NuGet (`GoogleTranslator2`) — free, keyless, wraps the same unofficial Google endpoint as `deep-translator`.
- Shipped as a self-contained single-file `.exe` (~51 MB) via `dotnet publish`.

## Common commands

```bash
dotnet build                           # compile everything
dotnet test                            # run all tests (xUnit)
dotnet test --filter "FullyQualifiedName~ChatLineParserTests.Parses_GermanTeamChat_WithCallout"   # single test
dotnet run --project src/CS2ChatTranslator   # launch the WinForms app from source
```

Publish the distributable `.exe`:

```bash
dotnet publish src/CS2ChatTranslator/CS2ChatTranslator.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o out/
```

The previous run's `.exe` holds a file lock while running — if `dotnet publish` fails with a file-in-use error, check `tasklist //FI "IMAGENAME eq CS2ChatTranslator.exe"` and kill the process before republishing.

## Architecture

Pipeline, top to bottom:

```
console.log  →  ConsoleLogTailer  →  ChatLineParser  →  TranslationService  →  MainForm (RichTextBox)
```

- **`Services/ConsoleLogTailer.cs`** — opens the file with `FileShare.ReadWrite | FileShare.Delete` (mandatory, otherwise CS2 can't keep writing), polls every 500 ms, tracks byte offset, buffers partial lines, handles truncation (CS2 restart shrinks the file). Emits `LineRead` events. By default the app constructs it with `startFromBeginning: true` so historical log content is parsed on launch; the `false` default is preserved for tests that assert tail-from-end semantics.
- **`Services/ChatLineParser.cs`** — this file carries non-obvious knowledge. Read it before touching chat-format logic:
  - CS2's client log format depends on the **game language**. German client emits `[ALLE]` (all-chat), `[AT]` (CT team, "Anti-Terror"), `[TOT]` (dead suffix). English emits `[ALL]`/`[CT]`/`[T]`/`[DEAD]`. Supported tokens are in `TypeMap` — **add new localizations there**, don't loosen the regex.
  - Each chat line is prefixed with `MM/DD HH:MM:SS ` timestamp. The regex treats it as optional so hand-pasted lines without timestamps still parse (useful in tests).
  - Player names contain a **U+200E bidi mark** (`‎`) after the name — strip before any comparison.
  - Team-chat callouts appear as `Player‎﹫<location>:` using **U+FE6B SMALL COMMERCIAL AT** (not a normal `@`). The parser extracts `Callout` into its own field; both `﹫` and regular `@` are accepted.
  - The `[TOT]`/`[DEAD]` marker is a **suffix after the player name**, not a prefix on the line (previous iteration got this wrong).
  - Non-chat system lines (`[RenderSystem]`, `[Client]`, `[Filesystem]`, …) are rejected by the whitelist. Widening the whitelist accidentally re-enables thousands of false positives.
  - `ChatLineParserTests.ExtractsOnlyChatLines_FromRealLogExcerpt` contains a real CS2 log slice — use it as the canonical regression test when changing the parser.
- **`Services/TranslationService.cs`** — wraps GTranslate's `GoogleTranslator2`. Auto-detects source, retries once on failure, returns `(Text, Failed, Skipped)`. Skips translation when detected source == target (so `en → en` doesn't call the API).
- **`Services/ConfigStore.cs`** — JSON round-trip at `%APPDATA%\CS2ChatTranslator\config.json` with camelCase properties, atomic write (temp file + `File.Move`). Returns defaults (`en`, empty path) when the file is missing.
- **`UI/MainForm.cs`** + `MainForm.Designer.cs` — dark `RichTextBox` chat feed; full re-render on every new message / translation completion (simple and correct for ≤500 messages, capped by `MaxMessages`). StatusStrip shows live counters (`📖 lines / 💬 chat / 🌐 translated`) which are the go-to diagnostic when "nothing shows up."
- **`UI/SettingsForm.cs`** — hand-built WinForms dialog. Language `ComboBox` is populated with `Items.AddRange` (not `DataSource`); the `DataSource` path leaves `Items.Count == 0` until a handle is created, which makes `SelectedIndex = 0` throw. Don't switch back to `DataSource`. Log path is picked via `OpenFileDialog` with an `InitialDirectory` heuristic that probes common Steam library locations.

## Threading model

- Tailer runs on `System.Threading.Timer` callbacks (ThreadPool).
- All UI mutation goes through `MainForm.BeginInvoke(...)`; state (`_messages`, counters) is mutated only on the UI thread, so no locks.
- Translation is fire-and-forget `async void HandleNewMessage` — the message is appended to the feed immediately as `[Übersetze…]`, and the translation text replaces the placeholder when the task completes. Out-of-order completions are handled by re-rendering the full feed from the in-memory list.

## Platform notes for this repo

- Dev shell is **Git Bash on Windows**. Use forward slashes; `/dev/null` not `NUL`. Windows paths in JSON need `\\` escaping.
- Test project also targets `net10.0-windows` (not plain `net10.0`) because the app project is Windows-only and the test project references it. Don't change this back.
- The nullable `components` field in `MainForm.Designer.cs` requires `#nullable enable` at the top — Designer files are treated as generated code and don't inherit the project-level context.
- Distributing the `.exe`: SmartScreen/Defender routinely flag unsigned single-file .NET binaries. This is expected; code signing is out of scope. `.pdb` is not needed by end users.

## Git

- Default branch is `main` on GitHub (`Klausiiiii/CS2-Chat-Translator`). Local `master` was renamed to `main`.
- `.gitignore` covers `bin/ obj/ out/ *.user .vs/ .idea/` — keep build artifacts out of commits.
