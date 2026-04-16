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
