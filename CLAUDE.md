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

- **`Services/ConsoleLogTailer.cs`** — opens the file with `FileShare.ReadWrite | FileShare.Delete` (mandatory, otherwise CS2 can't keep writing), polls every 500 ms, tracks byte offset, buffers partial lines, handles truncation (CS2 restart shrinks the file). Emits `LineRead` events. By default the app constructs it with `startFromBeginning: true` so historical log content is parsed on launch; the `false` default is preserved for tests that assert tail-from-end semantics.
- **`Services/ChatLineParser.cs`** — this file carries non-obvious knowledge. Read it before touching chat-format logic:
  - CS2's client log format depends on the **game language**. German client emits `[ALLE]` (all-chat), `[AT]` (CT team, "Anti-Terror"), `[TOT]` (dead suffix). English emits `[ALL]`/`[CT]`/`[T]`/`[DEAD]`. Supported tokens are in `TypeMap` — **add new localizations there**, don't loosen the regex.
  - Each chat line is prefixed with `MM/DD HH:MM:SS ` timestamp. The regex treats it as optional so hand-pasted lines without timestamps still parse (useful in tests).
  - Player names contain a **U+200E bidi mark** (`‎`) after the name — strip before any comparison.
  - Team-chat callouts appear as `Player‎﹫<location>:` using **U+FE6B SMALL COMMERCIAL AT** (not a normal `@`). The parser extracts `Callout` into its own field; both `﹫` and regular `@` are accepted.
  - The `[TOT]`/`[DEAD]` marker is a **suffix after the player name**, not a prefix on the line (previous iteration got this wrong).
  - Non-chat system lines (`[RenderSystem]`, `[Client]`, `[Filesystem]`, …) are rejected by the whitelist. Widening the whitelist accidentally re-enables thousands of false positives.
  - `ChatLineParserTests.ExtractsOnlyChatLines_FromRealLogExcerpt` contains a real CS2 log slice — use it as the canonical regression test when changing the parser.
- **`Services/TranslationService.cs`** — wraps GTranslate's `GoogleTranslator2`. Auto-detects source, retries once on failure, returns `(Text, Failed, Skipped)`. The API is **always** called (source detection happens inside the same roundtrip); `Skipped=true` is set when the detected source language matches the target, and the returned `Text` is the original input. Today no UI reads `Skipped`, so an `en → en` message renders with its "translation" row duplicating the original. If that ever becomes annoying, branch on `Skipped` in the UI before updating `_translated` / rendering the arrow row.
- **`Services/ConfigStore.cs`** — JSON round-trip at `%APPDATA%\CS2ChatTranslator\config.json` (Windows) / `~/.config/CS2ChatTranslator/config.json` (Linux) with camelCase properties, atomic write (temp file + `File.Move`). Returns defaults (`en`, empty path) when the file is missing. `Environment.SpecialFolder.ApplicationData` maps to both correctly — don't hardcode platform paths.
- **`Services/SteamPaths.cs`** — `FindExistingCsgoDirectory()` probes common Steam library locations per platform. Windows checks `Program Files`/`Program Files (x86)` + common secondary drives. Linux checks `~/.local/share/Steam`, `~/.steam/steam`, `~/.steam/root`, and the Flatpak path (`~/.var/app/com.valvesoftware.Steam/data/Steam`). **Add new platform roots here**, not in the UI layer.

### WinForms UI (`src/CS2ChatTranslator/`)

- **`UI/MainForm.cs`** + `MainForm.Designer.cs` — dark `RichTextBox` chat feed; full re-render on every new message / translation completion (simple and correct for ≤500 messages, capped by `MaxMessages`). StatusStrip shows live counters (`📖 lines / 💬 chat / 🌐 translated`) which are the go-to diagnostic when "nothing shows up."
- **`UI/SettingsForm.cs`** — hand-built WinForms dialog. Language `ComboBox` is populated with `Items.AddRange` (not `DataSource`); the `DataSource` path leaves `Items.Count == 0` until a handle is created, which makes `SelectedIndex = 0` throw. Don't switch back to `DataSource`.

### Avalonia UI (`src/CS2ChatTranslator.Avalonia/`)

- **`Views/MainWindow.axaml[.cs]`** — DockPanel with Menu, ScrollViewer + `SelectableTextBlock` for the feed, and a bottom status bar. The feed uses `Inlines` with `Run` elements (Avalonia's equivalent to RichTextBox colored runs). Same 500-message cap, same full-re-render strategy. `Dispatcher.UIThread.Post` is the BeginInvoke equivalent.
- **`Views/SettingsWindow.axaml[.cs]`** — ComboBox + readonly TextBox + Browse button. File picking goes through `StorageProvider.OpenFilePickerAsync` (async, unlike WinForms' `OpenFileDialog`). Dialog returns `AppConfig?` via `Close(result)` / `ShowDialog<AppConfig?>()`. Warning popups are inline `Window` + `TextBlock` (Avalonia has no built-in `MessageBox`).
- **`App.axaml`** — Fluent theme, dark variant requested globally. Don't switch to Simple theme; the dark look breaks.
- Namespaces: the project folder is `CS2ChatTranslator.Avalonia`, but the C# root namespace is `CS2ChatTranslator` to avoid colliding with the `Avalonia` NuGet namespace. If you see `Avalonia.X` ambiguity, qualify with `global::Avalonia.X`.

## Threading model

- Tailer runs on `System.Threading.Timer` callbacks (ThreadPool).
- All UI mutation goes through the UI thread: WinForms uses `BeginInvoke`, Avalonia uses `Dispatcher.UIThread.Post`. State (`_messages`, counters) is mutated only on the UI thread, so no locks.
- Translation is fire-and-forget `async void HandleNewMessage` — the message is appended to the feed immediately as `[Übersetze…]`, and the translation text replaces the placeholder when the task completes. Out-of-order completions are handled by re-rendering the full feed from the in-memory list.

## Platform notes for this repo

- Dev shell is **Git Bash on Windows**. Use forward slashes; `/dev/null` not `NUL`. Windows paths in JSON need `\\` escaping.
- The WinForms test project targeting changed — tests now target `net10.0` and reference `CS2ChatTranslator.Core`, **not** the Windows-only WinForms project. Don't add a reference back to the WinForms project or you'll re-introduce the Windows constraint on tests.
- The nullable `components` field in `MainForm.Designer.cs` requires `#nullable enable` at the top — Designer files are treated as generated code and don't inherit the project-level context.
- Avalonia pulls a known-vulnerable `Tmds.DBus.Protocol 0.20.0` as a transitive dep (NU1903 warning). It's only hit on Linux at runtime and Avalonia upstream will bump it; we don't pin it here. If you add a Linux release pipeline, consider an explicit `PackageReference` to a fixed version.
- Distributing binaries: SmartScreen/Defender routinely flag unsigned single-file .NET binaries. This is expected; code signing is out of scope. `.pdb` is not needed by end users.

## Git

- Default branch is `main` on GitHub (`Klausiiiii/CS2-Chat-Translator`). Local `master` was renamed to `main`.
- `.gitignore` covers `bin/ obj/ out/ *.user .vs/ .idea/` — keep build artifacts out of commits.
