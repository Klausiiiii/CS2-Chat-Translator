# CS2 Chat Translator

A small desktop tool that reads Counter-Strike 2's in-game chat and translates foreign-language messages into your language in real time — in a separate window, with both original text and translation visible.

## What it does

- Tails CS2's `console.log` (produced when the game is launched with the `-condebug` option).
- Pulls **all-chat and team-chat** messages, keeping who wrote them, which channel, and team-chat location callouts.
- Translates each message into your configured target language via free Google Translate (no API key required).
- Renders original + translation in a dark chat feed, with a live status bar showing lines read / chat found / translated.

Currently recognizes **English** (`[ALL]` / `[CT]` / `[T]`) and **German** (`[ALLE]` / `[AT]` / `[T]`, incl. `[TOT]` dead suffix) CS2 client localizations, plus a few others. More can be added in `Services/ChatLineParser.cs` (the `TypeMap` dictionary).

## Quick start

1. Download the binary for your platform from the [Releases](https://github.com/Klausiiiii/CS2-Chat-Translator/releases) page — or build it yourself (see below).
2. Launch CS2 with the **`-condebug`** launch option so the game writes `console.log`.
3. Run the app.
4. Open **Datei → Einstellungen…**:
   - Pick your target language.
   - **Durchsuchen…** to your `console.log`. Typical locations:
     - Windows: `...\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\console.log`
     - Linux:   `~/.local/share/Steam/steamapps/common/Counter-Strike Global Offensive/game/csgo/console.log`
5. Click **OK**. Chat messages from your matches now appear in the window translated.

Settings persist across launches:
- Windows: `%APPDATA%\CS2ChatTranslator\config.json`
- Linux:   `~/.config/CS2ChatTranslator/config.json`

## Platforms

| Platform | UI frontend    | Project                              |
|----------|----------------|--------------------------------------|
| Windows  | WinForms       | `src/CS2ChatTranslator/`             |
| Windows  | Avalonia       | `src/CS2ChatTranslator.Avalonia/`    |
| Linux    | Avalonia       | `src/CS2ChatTranslator.Avalonia/`    |

The WinForms build is the original / canonical Windows binary. The Avalonia build runs on both Linux and Windows and shares the same services/parser code via `CS2ChatTranslator.Core`.

## Known limitations

- x64 only — no ARM builds shipped.
- Needs an internet connection (Google Translate).
- Unsigned binary — Windows SmartScreen / Defender may warn on first launch. Click "More info → Run anyway" or whitelist it.
- Only the 500 most recent messages are kept in the feed; older ones scroll out.
- Translation quality is whatever free Google Translate provides; slang and CS jargon sometimes come out rough.

## Build from source

Requires the **.NET 10 SDK**.

```bash
git clone https://github.com/Klausiiiii/CS2-Chat-Translator.git
cd CS2-Chat-Translator
dotnet build
dotnet test
```

Run from source without building a binary:

```bash
# Windows WinForms
dotnet run --project src/CS2ChatTranslator

# Windows / Linux Avalonia
dotnet run --project src/CS2ChatTranslator.Avalonia
```

### Publish a self-contained binary

Windows WinForms (~51 MB, no .NET runtime needed on the target machine):

```bash
dotnet publish src/CS2ChatTranslator/CS2ChatTranslator.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o out/win-winforms/
```

Linux Avalonia:

```bash
dotnet publish src/CS2ChatTranslator.Avalonia/CS2ChatTranslator.Avalonia.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o out/linux/
```

Windows Avalonia (same command, different RID):

```bash
dotnet publish src/CS2ChatTranslator.Avalonia/CS2ChatTranslator.Avalonia.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o out/win-avalonia/
```

## Project layout

- `src/CS2ChatTranslator.Core/` — shared library: log tailer, chat parser, translator, config store, Steam path helper. Cross-platform (`net10.0`).
- `src/CS2ChatTranslator/` — WinForms Windows app (`net10.0-windows`).
- `src/CS2ChatTranslator.Avalonia/` — Avalonia cross-platform app (`net10.0`), runs on Linux and Windows.
- `tests/CS2ChatTranslator.Tests/` — xUnit tests against Core, incl. a regression test that parses a real CS2 log excerpt.
- `CLAUDE.md` — architecture notes and parser gotchas for contributors.
