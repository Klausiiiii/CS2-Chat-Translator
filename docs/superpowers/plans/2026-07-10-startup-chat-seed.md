# Startup Chat Seed Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On startup, show and translate the last ~25 chat messages from the current CS2 session instead of an empty feed.

**Architecture:** The `ConsoleLogTailer` reads a bounded 1 MiB tail window from the file end at start and emits its complete lines as a single `SeedRead` batch, then tails live as before. A pure Core function caps that batch to the last N parsed chat messages. Each UI renders those immediately and fires their translations staggered to spare the keyless endpoint.

**Tech Stack:** .NET 10, C#, xUnit. WinForms (`net10.0-windows`) + Avalonia (`net10.0`) UIs, shared `CS2ChatTranslator.Core`.

## Global Constraints

- Tailer must open with `FileShare.ReadWrite | FileShare.Delete` (CS2 keeps writing) — unchanged.
- Default tailer behavior must stay identical when `seedTailBytes` is unset (0): existing `ConsoleLogTailerTests` stay green.
- Decoding stays stateful via the shared `_decoder`; call `_decoder.Reset()` on any discontinuous seek.
- No new user setting. Seed parameters are constants: window = 1 MiB (`1 << 20`), N = 25, stagger delay = 200 ms.
- Chat parsing lives only in `ChatLineParser`; the tailer stays chat-agnostic (emits raw lines).
- UI mutation only on the UI thread (WinForms `BeginInvoke`, Avalonia `Dispatcher.UIThread.Post`).
- Don't duplicate service logic across the two UIs — shared logic goes in Core.

---

### Task 1: Tailer — seed window + `SeedRead` batch event

**Files:**
- Modify: `src/CS2ChatTranslator.Core/Services/ConsoleLogTailer.cs`
- Test: `tests/CS2ChatTranslator.Tests/ConsoleLogTailerTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `public const long DefaultSeedTailBytes = 1 << 20;`
  - ctor overload `ConsoleLogTailer(string path, TimeSpan? pollInterval = null, bool startFromBeginning = false, long seedTailBytes = 0)`
  - `event EventHandler<IReadOnlyList<string>>? SeedRead;` — fired once, synchronously, during `Start()` before live tailing begins, carrying the complete lines of the tail window in file order.

- [ ] **Step 1: Write the failing tests**

Add to `tests/CS2ChatTranslator.Tests/ConsoleLogTailerTests.cs`:

```csharp
[Fact]
public async Task Seed_EmitsLastLines_AsBatch()
{
    File.WriteAllText(_path, "[ALL] a: one\n[ALL] b: two\n[ALL] c: three\n", Encoding.UTF8);
    IReadOnlyList<string>? batch = null;
    using var tailer = new ConsoleLogTailer(_path, TimeSpan.FromMilliseconds(50), seedTailBytes: 1 << 20);
    tailer.SeedRead += (_, lines) => batch = lines;
    tailer.Start();

    Assert.NotNull(batch);
    Assert.Equal(new[] { "[ALL] a: one", "[ALL] b: two", "[ALL] c: three" }, batch);
    await Task.CompletedTask;
}

[Fact]
public async Task Seed_DiscardsPartialFirstLine_WhenWindowStartsMidFile()
{
    // Window smaller than the file: the first line is sliced mid-way and must be dropped.
    File.WriteAllText(_path, "[ALL] old: AAAAAAAAAA\n[ALL] new: keep\n", Encoding.UTF8);
    IReadOnlyList<string>? batch = null;
    using var tailer = new ConsoleLogTailer(_path, TimeSpan.FromMilliseconds(50), seedTailBytes: 20);
    tailer.SeedRead += (_, lines) => batch = lines;
    tailer.Start();

    Assert.NotNull(batch);
    Assert.DoesNotContain(batch!, l => l.Contains("old"));
    Assert.Contains("[ALL] new: keep", batch!);
    await Task.CompletedTask;
}

[Fact]
public async Task Seed_ThenTailsLiveContent_NoGapNoDuplication()
{
    File.WriteAllText(_path, "[ALL] a: seed1\n[ALL] b: seed2\n", Encoding.UTF8);
    IReadOnlyList<string>? batch = null;
    var live = new List<string>();
    using var tailer = new ConsoleLogTailer(_path, TimeSpan.FromMilliseconds(50), seedTailBytes: 1 << 20);
    tailer.SeedRead += (_, lines) => batch = lines;
    tailer.LineRead += (_, line) => { lock (live) live.Add(line); };
    tailer.Start();

    File.AppendAllText(_path, "[ALL] c: live1\n", Encoding.UTF8);
    await WaitUntil(() => live.Contains("[ALL] c: live1"), TimeSpan.FromSeconds(3));

    Assert.Equal(new[] { "[ALL] a: seed1", "[ALL] b: seed2" }, batch);
    Assert.Contains("[ALL] c: live1", live);                    // live tailing continues
    Assert.DoesNotContain(live, l => l.Contains("seed"));        // seed not re-emitted as live
}

[Fact]
public async Task Seed_Disabled_ByDefault_FiresNoSeedEvent()
{
    File.WriteAllText(_path, "[ALL] a: one\n[ALL] b: two\n", Encoding.UTF8);
    var fired = false;
    using var tailer = new ConsoleLogTailer(_path, TimeSpan.FromMilliseconds(50));
    tailer.SeedRead += (_, _) => fired = true;
    tailer.Start();

    await Task.Delay(150);
    Assert.False(fired);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ConsoleLogTailerTests.Seed"`
Expected: FAIL — compile error (`seedTailBytes`/`SeedRead` don't exist yet).

- [ ] **Step 3: Add the field, const, ctor parameter, and event**

In `ConsoleLogTailer.cs`, add near the other constants/fields:

```csharp
public const long DefaultSeedTailBytes = 1 << 20;
```

Add field beside `_startFromBeginning`:

```csharp
private readonly long _seedTailBytes;
```

Add the event beside `LineRead`:

```csharp
/// <summary>Fired once during Start() with the complete lines of the tail window, in file order.</summary>
public event EventHandler<IReadOnlyList<string>>? SeedRead;
```

Extend the ctor:

```csharp
public ConsoleLogTailer(string path, TimeSpan? pollInterval = null, bool startFromBeginning = false, long seedTailBytes = 0)
{
    _path = path;
    _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
    _startFromBeginning = startFromBeginning;
    _seedTailBytes = seedTailBytes;
    _timer = new System.Threading.Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
}
```

- [ ] **Step 4: Extract a shared line-splitting helper and route `ReadNewBytes` through it**

To keep the seed path and the live path from drifting on `\r\n`/empty-line rules, extract the splitting loop into one private helper used by both. Add this method:

```csharp
// Emits each complete, non-empty (\r-trimmed) line in [startIdx, lastNewline] via onLine and
// returns the trailing partial (substring after the last '\n'). If there is no '\n' at/after
// startIdx, returns the whole remainder from startIdx unchanged. Shared by the live read path
// and the seed path so the line-splitting rules stay identical.
private string EmitCompleteLines(string text, int startIdx, Action<string> onLine)
{
    var lastNewline = text.LastIndexOf('\n');
    if (lastNewline < startIdx) return text.Substring(startIdx);

    var start = startIdx;
    while (start <= lastNewline)
    {
        var nl = text.IndexOf('\n', start);
        if (nl < 0) break;
        var lineLen = nl - start;
        if (lineLen > 0 && text[nl - 1] == '\r') lineLen--;
        if (lineLen > 0)
        {
            LinesRead++;
            onLine(text.Substring(start, lineLen));
        }
        start = nl + 1;
    }
    return text.Substring(lastNewline + 1);
}
```

Now replace the tail of `ReadNewBytes` (from `var combined = _partialLine + chunk;` to the end of the method) with the helper call. The `MaxPartialLine` cap stays here — it is specific to the live path:

```csharp
var combined = _partialLine + chunk;

if (combined.LastIndexOf('\n') < 0)
{
    // Bound the accumulator: a newline-free stream (misconfigured/binary file) would otherwise
    // grow _partialLine without limit and make `combined = _partialLine + chunk` O(N^2). Real
    // CS2 chat lines are well under this cap; discard and resync on the next '\n'.
    _partialLine = combined.Length > MaxPartialLine ? "" : combined;
    return;
}

_partialLine = EmitCompleteLines(combined, 0, line => LineRead?.Invoke(this, line));
```

- [ ] **Step 5: Emit the seed in `Start()` using the helper**

Change `Start()` to run the seed after opening, before arming the timer:

```csharp
public void Start()
{
    OpenAndSeek();
    if (_seedTailBytes > 0) EmitSeed();
    _timer.Change(TimeSpan.Zero, _pollInterval);
}
```

Add the `EmitSeed` method. It re-seeks to the window start, decodes with a reset decoder, drops the sliced first line when it started mid-file, splits complete lines into a batch via the shared helper, keeps the trailing partial for live tailing, and leaves `_lastPosition` at the true end so no live bytes are lost or duplicated:

```csharp
private void EmitSeed()
{
    if (_stream is null) return;

    var length = _stream.Length;
    var offset = Math.Max(0, length - _seedTailBytes);
    var toRead = (int)(length - offset);
    _stream.Seek(offset, SeekOrigin.Begin);
    _decoder.Reset(); // window start is a discontinuity
    _lastPosition = length;
    _partialLine = "";
    if (toRead <= 0) return;

    string text;
    var buffer = ArrayPool<byte>.Shared.Rent(toRead);
    char[]? charBuffer = null;
    try
    {
        var read = _stream.Read(buffer, 0, toRead);
        _lastPosition = offset + read;
        var charCount = _decoder.GetCharCount(buffer, 0, read, flush: false);
        charBuffer = ArrayPool<char>.Shared.Rent(charCount == 0 ? 1 : charCount);
        var produced = _decoder.GetChars(buffer, 0, read, charBuffer, 0, flush: false);
        text = new string(charBuffer, 0, produced);
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
        if (charBuffer is not null) ArrayPool<char>.Shared.Return(charBuffer);
    }

    // If we seeked into the middle of the file, the first (partial) line is garbage — resync past it.
    var startIdx = 0;
    if (offset > 0)
    {
        var firstNl = text.IndexOf('\n');
        if (firstNl < 0) { _partialLine = ""; return; } // no complete line in window
        startIdx = firstNl + 1;
    }

    var lines = new List<string>();
    _partialLine = EmitCompleteLines(text, startIdx, lines.Add);
    if (lines.Count > 0) SeedRead?.Invoke(this, lines);
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ConsoleLogTailerTests"`
Expected: PASS — all seed tests plus the pre-existing tailer tests (which also cover the `ReadNewBytes` refactor to `EmitCompleteLines`).

- [ ] **Step 7: Commit**

```bash
git add src/CS2ChatTranslator.Core/Services/ConsoleLogTailer.cs tests/CS2ChatTranslator.Tests/ConsoleLogTailerTests.cs
git commit -m "feat: emit bounded tail-window seed batch from ConsoleLogTailer"
```

---

### Task 2: Core — `ChatSeedSelector` last-N cap

**Files:**
- Create: `src/CS2ChatTranslator.Core/Services/ChatSeedSelector.cs`
- Test: `tests/CS2ChatTranslator.Tests/ChatSeedSelectorTests.cs`

**Interfaces:**
- Consumes: `ChatLineParser.TryParse(string?, out ChatMessage?)`, `ChatMessage`.
- Produces: `public static IReadOnlyList<ChatMessage> ChatSeedSelector.LastMessages(IReadOnlyList<string> rawLines, int maxCount)` — parses each raw line, keeps chat hits, returns the last `maxCount` in original (chronological) order.

- [ ] **Step 1: Write the failing tests**

Create `tests/CS2ChatTranslator.Tests/ChatSeedSelectorTests.cs`:

```csharp
using CS2ChatTranslator.Services;

namespace CS2ChatTranslator.Tests;

public class ChatSeedSelectorTests
{
    [Fact]
    public void KeepsOnlyLastN_ChatLines_InOrder()
    {
        var raw = new[]
        {
            "[ALL] a: 1",
            "[RenderSystem] noise",
            "[ALL] b: 2",
            "[Client] noise",
            "[ALL] c: 3",
            "[ALL] d: 4",
        };

        var result = ChatSeedSelector.LastMessages(raw, 2);

        Assert.Equal(2, result.Count);
        Assert.Equal("c", result[0].Player);
        Assert.Equal("d", result[1].Player);
    }

    [Fact]
    public void FewerThanN_ReturnsAll()
    {
        var raw = new[] { "[ALL] a: 1", "[Client] noise", "[ALL] b: 2" };
        var result = ChatSeedSelector.LastMessages(raw, 25);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void NoChatLines_ReturnsEmpty()
    {
        var raw = new[] { "[RenderSystem] x", "[Client] y" };
        var result = ChatSeedSelector.LastMessages(raw, 25);
        Assert.Empty(result);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ChatSeedSelectorTests"`
Expected: FAIL — `ChatSeedSelector` does not exist.

- [ ] **Step 3: Implement `ChatSeedSelector`**

Create `src/CS2ChatTranslator.Core/Services/ChatSeedSelector.cs`:

```csharp
using CS2ChatTranslator.Models;

namespace CS2ChatTranslator.Services;

/// <summary>
/// Pure selection of the last N chat messages from a batch of raw log lines.
/// Kept out of the UI so the cap behavior is hermetically testable.
/// </summary>
public static class ChatSeedSelector
{
    public static IReadOnlyList<ChatMessage> LastMessages(IReadOnlyList<string> rawLines, int maxCount)
    {
        if (maxCount <= 0 || rawLines.Count == 0) return Array.Empty<ChatMessage>();

        var parsed = new List<ChatMessage>();
        foreach (var line in rawLines)
        {
            if (ChatLineParser.TryParse(line, out var msg) && msg is not null)
                parsed.Add(msg);
        }

        if (parsed.Count > maxCount)
            parsed.RemoveRange(0, parsed.Count - maxCount);

        return parsed;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ChatSeedSelectorTests"`
Expected: PASS — all three.

- [ ] **Step 5: Commit**

```bash
git add src/CS2ChatTranslator.Core/Services/ChatSeedSelector.cs tests/CS2ChatTranslator.Tests/ChatSeedSelectorTests.cs
git commit -m "feat: add ChatSeedSelector last-N cap"
```

---

### Task 3: WinForms — wire seed + staggered translation

**Files:**
- Modify: `src/CS2ChatTranslator/UI/MainForm.cs`

**Interfaces:**
- Consumes: `ConsoleLogTailer.SeedRead`, `ConsoleLogTailer.DefaultSeedTailBytes`, `ChatSeedSelector.LastMessages`.
- Produces: no new public surface (internal UI wiring).

**Note on testing:** WinForms UI wiring has no unit-test harness in this repo. It is covered by the Core tests (Tasks 1–2) plus a build and a manual smoke check in Task 5. Do not fabricate a UI unit test.

- [ ] **Step 1: Add seed constants**

Near the top of the `MainForm` class, beside existing constants (e.g. `MaxMessages`):

```csharp
private const int SeedMaxMessages = 25;
private const int SeedStaggerDelayMs = 200;
```

- [ ] **Step 2: Construct the tailer with the seed window and subscribe**

In the tailer setup (currently around `MainForm.cs:87`), change construction and add the subscription:

```csharp
_tailer = new ConsoleLogTailer(
    _config.ConsoleLogPath,
    startFromBeginning: false,
    seedTailBytes: ConsoleLogTailer.DefaultSeedTailBytes);
_tailer.LineRead += OnLineRead;
_tailer.SeedRead += OnSeedRead;
_tailer.ErrorOccurred += OnTailerError;
_tailer.Start();
```

- [ ] **Step 3: Extract the translation step out of `HandleNewMessage`**

Refactor `HandleNewMessage` so the translation half becomes a reusable `TranslateMessage`. Replace the existing `HandleNewMessage` body's inner try/catch + `UpdateMessageTranslation` with a call:

```csharp
private async void HandleNewMessage(ChatMessage msg)
{
    try
    {
        _chatFound++;
        _messages.Add(msg);
        AppendMessage(msg);
        if (_messages.Count > MaxMessages) TrimOldest(_messages.Count - MaxMessages);
        await TranslateMessage(msg);
    }
    catch
    {
        // never let a UI-mutation exception escape an async void handler
    }
}

private async Task TranslateMessage(ChatMessage msg)
{
    try
    {
        var result = await _translator.TranslateAsync(msg.Original, _config.TargetLanguage);
        msg.Translation = result.Text;
        msg.TranslationFailed = result.Failed;
        msg.SourceLanguage = result.SourceLanguage;
        if (!result.Failed) _translated++;
    }
    catch
    {
        msg.Translation = msg.Original;
        msg.TranslationFailed = true;
    }
    if (IsHandleCreated) UpdateMessageTranslation(msg);
}
```

- [ ] **Step 4: Add the seed handlers**

```csharp
private void OnSeedRead(object? sender, IReadOnlyList<string> lines)
{
    var msgs = ChatSeedSelector.LastMessages(lines, SeedMaxMessages);
    if (msgs.Count == 0 || !IsHandleCreated) return;
    BeginInvoke(() => HandleSeed(msgs));
}

private async void HandleSeed(IReadOnlyList<ChatMessage> msgs)
{
    try
    {
        foreach (var m in msgs)
        {
            _chatFound++;
            _messages.Add(m);
            AppendMessage(m);
        }
        if (_messages.Count > MaxMessages) TrimOldest(_messages.Count - MaxMessages);

        // Stagger translation starts so the keyless endpoint isn't hit with a burst.
        foreach (var m in msgs)
        {
            _ = TranslateMessage(m);
            await Task.Delay(SeedStaggerDelayMs);
        }
    }
    catch
    {
        // never let a UI-mutation exception escape an async void handler
    }
}
```

- [ ] **Step 5: Build and verify**

Run: `dotnet build src/CS2ChatTranslator/CS2ChatTranslator.csproj -c Debug`
Expected: `0 Fehler` / build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/CS2ChatTranslator/UI/MainForm.cs
git commit -m "feat: seed WinForms feed with last N messages, staggered translation"
```

---

### Task 4: Avalonia — wire seed + staggered translation

**Files:**
- Modify: `src/CS2ChatTranslator.Avalonia/Views/MainWindow.axaml.cs`

**Interfaces:**
- Consumes: `ConsoleLogTailer.SeedRead`, `ConsoleLogTailer.DefaultSeedTailBytes`, `ChatSeedSelector.LastMessages`.
- Produces: no new public surface.

**Note on testing:** Same as Task 3 — no Avalonia UI unit-test harness; covered by Core tests + build + manual smoke in Task 5.

- [ ] **Step 1: Add seed constants**

Beside the existing `MaxMessages` constant in `MainWindow`:

```csharp
private const int SeedMaxMessages = 25;
private const int SeedStaggerDelayMs = 200;
```

- [ ] **Step 2: Construct the tailer with the seed window and subscribe**

At `MainWindow.axaml.cs:159`, change construction and subscribe:

```csharp
_tailer = new ConsoleLogTailer(
    _config.ConsoleLogPath,
    startFromBeginning: false,
    seedTailBytes: ConsoleLogTailer.DefaultSeedTailBytes);
_tailer.LineRead += OnLineRead;
_tailer.SeedRead += OnSeedRead;
_tailer.ErrorOccurred += OnTailerError;
_tailer.Start();
```

- [ ] **Step 3: Extract the translation step out of `HandleNewMessage`**

```csharp
private async void HandleNewMessage(ChatMessage msg)
{
    try
    {
        _chatFound++;
        _messages.Add(msg);
        AppendMessage(msg);
        if (_messages.Count > MaxMessages) TrimOldest(_messages.Count - MaxMessages);
        await TranslateMessage(msg);
    }
    catch
    {
        // never let a UI-mutation exception escape an async void handler
    }
}

private async Task TranslateMessage(ChatMessage msg)
{
    try
    {
        var result = await _translator.TranslateAsync(msg.Original, _config.TargetLanguage);
        msg.SourceLanguage = result.SourceLanguage;
        msg.Translation = result.Text;
        msg.TranslationFailed = result.Failed;
        if (!result.Failed) _translated++;
    }
    catch
    {
        msg.Translation = msg.Original;
        msg.TranslationFailed = true;
    }
    UpdateMessageTranslation(msg);
}
```

- [ ] **Step 4: Add the seed handlers**

```csharp
private void OnSeedRead(object? sender, IReadOnlyList<string> lines)
{
    var msgs = ChatSeedSelector.LastMessages(lines, SeedMaxMessages);
    if (msgs.Count == 0) return;
    Dispatcher.UIThread.Post(() => HandleSeed(msgs));
}

private async void HandleSeed(IReadOnlyList<ChatMessage> msgs)
{
    try
    {
        foreach (var m in msgs)
        {
            _chatFound++;
            _messages.Add(m);
            AppendMessage(m);
        }
        if (_messages.Count > MaxMessages) TrimOldest(_messages.Count - MaxMessages);

        // Stagger translation starts so the keyless endpoint isn't hit with a burst.
        foreach (var m in msgs)
        {
            _ = TranslateMessage(m);
            await Task.Delay(SeedStaggerDelayMs);
        }
    }
    catch
    {
        // never let a UI-mutation exception escape an async void handler
    }
}
```

- [ ] **Step 5: Build and verify**

Run: `dotnet build src/CS2ChatTranslator.Avalonia/CS2ChatTranslator.Avalonia.csproj -c Debug`
Expected: `0 Fehler` / build succeeded (NU1903 warning is already fixed on this branch).

- [ ] **Step 6: Commit**

```bash
git add src/CS2ChatTranslator.Avalonia/Views/MainWindow.axaml.cs
git commit -m "feat: seed Avalonia feed with last N messages, staggered translation"
```

---

### Task 5: Docs + full verification

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update the `ConsoleLogTailer` note in `CLAUDE.md`**

In the `Services/ConsoleLogTailer.cs` bullet, append a sentence documenting the seed:

```
Optionally seeds the feed on start: constructed with `seedTailBytes` > 0 (both UIs pass `DefaultSeedTailBytes` = 1 MiB), it reads a bounded tail window, drops the sliced first line, and fires a one-shot `SeedRead(IReadOnlyList<string>)` batch of the window's complete lines before live tailing — leaving `_lastPosition`/`_partialLine` positioned so live `LineRead` continues without gap or duplication. The UI caps that batch to the last 25 chat messages via `ChatSeedSelector.LastMessages` and fires their translations staggered (200 ms) to avoid a startup burst on the keyless endpoint.
```

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test`
Expected: PASS — all prior tests plus the new seed/selector tests (green, 0 failures).

- [ ] **Step 3: Build the whole solution in Release**

Run: `dotnet build -c Release`
Expected: `0 Fehler`, no NU1903.

- [ ] **Step 4: Manual smoke check (document the result)**

Create a temp log with >25 chat lines mixed with system noise, point a dev run at it (`dotnet run --project src/CS2ChatTranslator.Avalonia`), and confirm the feed shows the last ~25 messages translating in staggered, then live-tails an appended line. Note the observed result in the commit message. (If a headless environment blocks a GUI run, state that explicitly instead of claiming success.)

- [ ] **Step 5: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: document startup chat seed in CLAUDE.md"
```

---

### Task 6: Release 1.2.0 (security fix + seed feature)

**Files:** none (build + publish only).

**Context:** This ships the already-committed NU1903 fix (`221abda`) together with the seed feature. Version is **1.2.0** (new feature → minor bump). The 1.1.0 Linux binary still bundles the vulnerable `Tmds.DBus.Protocol 0.20.0`; the new 1.2.0 Linux binary replaces it.

- [ ] **Step 1: Merge/push the branch to main**

```bash
git push origin HEAD:main
```
Expected: fast-forward push (HEAD is ahead of origin/main by the feature commits).

- [ ] **Step 2: Build both binaries SEQUENTIALLY**

⚠️ Do NOT run these two publishes in parallel — they both rebuild the shared `Core.dll` and race on `obj/Release/.../CS2ChatTranslator.Core.dll` (error CS2012). Build one, then the other. When checking exit codes, use `set -o pipefail` + `${PIPESTATUS[0]}` — `dotnet ... | tail` reports `tail`'s exit code, hiding a publish failure.

```bash
rm -rf out/win-winforms out/linux
set -o pipefail
dotnet publish src/CS2ChatTranslator/CS2ChatTranslator.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o out/win-winforms/ 2>&1 | tail -3; echo "WIN_EXIT=${PIPESTATUS[0]}"
dotnet publish src/CS2ChatTranslator.Avalonia/CS2ChatTranslator.Avalonia.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o out/linux/ 2>&1 | tail -3; echo "LINUX_EXIT=${PIPESTATUS[0]}"
```
Expected: both exit 0; `out/win-winforms/CS2ChatTranslator.exe` and `out/linux/CS2ChatTranslator.Avalonia` present.

- [ ] **Step 3: Create the GitHub release**

Write release notes to a scratch file (features: startup seed; security: NU1903/Tmds pin), then:

```bash
gh release create 1.2.0 --target main --title "1.2.0" --notes-file <notes> \
  "out/win-winforms/CS2ChatTranslator.exe#CS2ChatTranslator.exe (Windows x64)" \
  "out/linux/CS2ChatTranslator.Avalonia#CS2ChatTranslator.Avalonia (Linux x64)"
```

- [ ] **Step 4: Verify the release**

```bash
gh release view 1.2.0 --json tagName,targetCommitish,assets --jq '{tag:.tagName, target:.targetCommitish, assets:[.assets[]|{name:.name, state:.state}]}'
gh release list --limit 3
```
Expected: both assets `uploaded`, 1.2.0 is `Latest`, tag on the main commit.

---

## Self-Review

**Spec coverage:**
- Bounded 1 MiB tail window + `SeedRead` batch → Task 1. ✓
- Resync of sliced first line → Task 1 (`Seed_DiscardsPartialFirstLine...`). ✓
- No gap/duplication into live tailing → Task 1 (`Seed_ThenTailsLiveContent...`). ✓
- Last-N cap as a pure Core function → Task 2. ✓
- Both UIs wired, no marker → Tasks 3, 4. ✓
- Staggered translation (~200 ms) → Tasks 3, 4. ✓
- Constants (1 MiB / 25 / 200 ms), no new setting → Tasks 1, 3, 4. ✓
- Default behavior unchanged → Task 1 (`Seed_Disabled_ByDefault...`) + existing tests. ✓
- Ship in one release with the NU1903 fix as 1.2.0 → Task 6. ✓

**Placeholder scan:** No TBD/TODO; every code step shows full code. The only non-code step (Task 5 smoke check) explicitly requires documenting the real observed result. ✓

**Type consistency:** `SeedRead` is `EventHandler<IReadOnlyList<string>>` in Task 1 and consumed as `(sender, IReadOnlyList<string> lines)` in Tasks 3/4. `ChatSeedSelector.LastMessages(IReadOnlyList<string>, int) → IReadOnlyList<ChatMessage>` defined in Task 2, called identically in Tasks 3/4. `TranslateMessage(ChatMessage) → Task` defined and called consistently. `DefaultSeedTailBytes` const defined in Task 1, used in Tasks 3/4. ✓
