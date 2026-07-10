using System.Text;
using CS2ChatTranslator.Services;

namespace CS2ChatTranslator.Tests;

public class ConsoleLogTailerTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"cs2-tailer-test-{Guid.NewGuid():N}.log");

    public ConsoleLogTailerTests()
    {
        File.WriteAllText(_path, "ignored historical content\n");
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public async Task Emits_LinesAppendedAfterStart()
    {
        var received = new List<string>();
        using var tailer = new ConsoleLogTailer(_path, TimeSpan.FromMilliseconds(50));
        tailer.LineRead += (_, line) => { lock (received) received.Add(line); };
        tailer.Start();

        await Task.Delay(100);

        File.AppendAllText(_path, "[ALL] A: hi\n[CT] B: ok\n", Encoding.UTF8);

        await WaitUntil(() => received.Count >= 2, TimeSpan.FromSeconds(3));

        Assert.Contains("[ALL] A: hi", received);
        Assert.Contains("[CT] B: ok", received);
    }

    [Fact]
    public async Task Does_NotReemitHistoricalContent()
    {
        var received = new List<string>();
        using var tailer = new ConsoleLogTailer(_path, TimeSpan.FromMilliseconds(50));
        tailer.LineRead += (_, line) => { lock (received) received.Add(line); };
        tailer.Start();

        await Task.Delay(300);

        Assert.Empty(received);
    }

    [Fact]
    public async Task Recovers_AfterTruncation()
    {
        var received = new List<string>();
        using var tailer = new ConsoleLogTailer(_path, TimeSpan.FromMilliseconds(50));
        tailer.LineRead += (_, line) => { lock (received) received.Add(line); };
        tailer.Start();

        await Task.Delay(100);
        File.AppendAllText(_path, "[ALL] pre: first\n", Encoding.UTF8);
        await WaitUntil(() => received.Count >= 1, TimeSpan.FromSeconds(2));

        // Simulate CS2 restart: truncate + write fresh content.
        using (var fs = new FileStream(_path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            var bytes = Encoding.UTF8.GetBytes("[ALL] post: second\n");
            fs.Write(bytes, 0, bytes.Length);
        }

        await WaitUntil(() => received.Contains("[ALL] post: second"), TimeSpan.FromSeconds(3));
        Assert.Contains("[ALL] post: second", received);
    }

    [Fact]
    public async Task Handles_PartialLineBuffering()
    {
        var received = new List<string>();
        using var tailer = new ConsoleLogTailer(_path, TimeSpan.FromMilliseconds(50));
        tailer.LineRead += (_, line) => { lock (received) received.Add(line); };
        tailer.Start();

        await Task.Delay(100);

        File.AppendAllText(_path, "[ALL] x: half", Encoding.UTF8);
        await Task.Delay(200);
        Assert.DoesNotContain(received, l => l.Contains("half"));

        File.AppendAllText(_path, "-message\n", Encoding.UTF8);
        await WaitUntil(() => received.Any(l => l.Contains("half-message")), TimeSpan.FromSeconds(2));
        Assert.Contains("[ALL] x: half-message", received);
    }

    [Fact]
    public async Task Decodes_MultibyteCodepoint_SplitAcrossReads()
    {
        var received = new List<string>();
        using var tailer = new ConsoleLogTailer(_path, TimeSpan.FromMilliseconds(50));
        tailer.LineRead += (_, line) => { lock (received) received.Add(line); };
        tailer.Start();

        await Task.Delay(100);

        // Split the Cyrillic line so a 2-byte codepoint ('п' = 0xD0 0xBF) straddles two reads:
        // 9 ASCII bytes of "[ALL] P: " + the first byte of 'п' in the first write.
        var full = Encoding.UTF8.GetBytes("[ALL] P: привет\n");
        const int splitAt = 10;
        AppendBytes(_path, full[..splitAt]);
        await Task.Delay(150); // guarantee a tick consumes the partial codepoint
        AppendBytes(_path, full[splitAt..]);

        await WaitUntil(() => received.Any(l => l.Contains("привет")), TimeSpan.FromSeconds(3));

        Assert.Contains("[ALL] P: привет", received);
        Assert.DoesNotContain(received, l => l.Contains('�')); // no replacement chars
    }

    [Fact]
    public async Task Decodes_Cleanly_AfterTruncationSplitsCodepoint()
    {
        var received = new List<string>();
        using var tailer = new ConsoleLogTailer(_path, TimeSpan.FromMilliseconds(50));
        tailer.LineRead += (_, line) => { lock (received) received.Add(line); };
        tailer.Start();

        await Task.Delay(100);

        // Leave a dangling half-codepoint in the decoder, then restart (truncate) the file.
        var full = Encoding.UTF8.GetBytes("[ALL] P: привет\n");
        AppendBytes(_path, full[..10]);
        await Task.Delay(150);

        using (var fs = new FileStream(_path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            var bytes = Encoding.UTF8.GetBytes("[ALL] Q: спасибо\n");
            fs.Write(bytes, 0, bytes.Length);
        }

        await WaitUntil(() => received.Any(l => l.Contains("спасибо")), TimeSpan.FromSeconds(3));

        // The decoder must be Reset on truncation, else the stale 0xD0 prepends garbage.
        Assert.Contains("[ALL] Q: спасибо", received);
        Assert.DoesNotContain(received, l => l.Contains('�'));
    }

    [Fact]
    public async Task Dispose_DuringActiveTailing_RaisesNoError()
    {
        for (var i = 0; i < 25; i++)
        {
            Exception? observed = null;
            var tailer = new ConsoleLogTailer(_path, TimeSpan.FromMilliseconds(1));
            tailer.ErrorOccurred += (_, ex) => observed = ex;
            tailer.Start();
            File.AppendAllText(_path, $"[ALL] x: line{i}\n", Encoding.UTF8);
            await Task.Delay(2);
            tailer.Dispose();        // must not race an in-flight tick into ObjectDisposedException
            await Task.Delay(5);
            Assert.Null(observed);
        }
    }

    [Fact]
    public async Task DiscardsOversizedPartialLine_AndResyncs()
    {
        var received = new List<string>();
        using var tailer = new ConsoleLogTailer(_path, TimeSpan.FromMilliseconds(50));
        tailer.LineRead += (_, line) => { lock (received) received.Add(line); };
        tailer.Start();

        await Task.Delay(100);

        // ~200 KB without a newline must be discarded, not retained/concatenated forever.
        File.AppendAllText(_path, new string('A', 200_000), Encoding.UTF8);
        await Task.Delay(200);
        File.AppendAllText(_path, "[ALL] x: hi\n", Encoding.UTF8);

        await WaitUntil(() => received.Any(l => l == "[ALL] x: hi"), TimeSpan.FromSeconds(3));

        Assert.Contains("[ALL] x: hi", received);                       // clean resync
        Assert.DoesNotContain(received, l => l.StartsWith("AAAA"));     // garbage was dropped
    }

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

    private static void AppendBytes(string path, byte[] bytes)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        fs.Write(bytes, 0, bytes.Length);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
    }
}
