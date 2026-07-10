using System.Text;
using CS2ChatTranslator.Services;

namespace CS2ChatTranslator.Tests;

/// <summary>
/// End-to-end coverage of the startup seed pipeline: ConsoleLogTailer's one-shot SeedRead batch
/// feeding ChatSeedSelector.LastMessages, followed by live LineRead tailing continuing without
/// gap or duplication. This is the Core-level boundary the UI layers (MainForm / MainWindow) sit
/// on top of — on-screen rendering itself (staggered placeholders, colors) is not covered here.
/// </summary>
public class SeedPipelineTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"cs2-seed-pipeline-test-{Guid.NewGuid():N}.log");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public async Task Seed_SelectsLast25ChatMessages_ChronologicalOrder_NoiseExcluded()
    {
        const int totalChatLines = 30; // > 25, so the selector's cap actually has to trim something.
        var sb = new StringBuilder();
        for (var i = 0; i < totalChatLines; i++)
        {
            sb.Append("[RenderSystem] frame ").Append(i).Append('\n');
            sb.Append("[Client] tick ").Append(i).Append('\n');
            sb.Append("[Filesystem] mount ").Append(i).Append('\n');
            sb.Append("[ALL] p").Append(i).Append(": message ").Append(i).Append('\n');
        }
        File.WriteAllText(_path, sb.ToString(), Encoding.UTF8);

        IReadOnlyList<string>? seedBatch = null;
        using var tailer = new ConsoleLogTailer(_path, TimeSpan.FromMilliseconds(50), seedTailBytes: ConsoleLogTailer.DefaultSeedTailBytes);
        tailer.SeedRead += (_, lines) => seedBatch = lines; // subscribed BEFORE Start(): SeedRead fires synchronously inside Start()
        tailer.Start();

        Assert.NotNull(seedBatch);
        // The 1 MiB window covers this whole (tiny) test file, so nothing is sliced off the front
        // and every raw line -- chat and noise alike -- comes back in the batch.
        Assert.Equal(totalChatLines * 4, seedBatch!.Count);

        var selected = ChatSeedSelector.LastMessages(seedBatch, 25);

        Assert.Equal(25, selected.Count);

        var expectedPlayers = Enumerable.Range(totalChatLines - 25, 25).Select(i => $"p{i}").ToArray();
        Assert.Equal(expectedPlayers, selected.Select(m => m.Player).ToArray());

        // Chronological order: the numeric suffix of each message strictly increases.
        for (var i = 1; i < selected.Count; i++)
        {
            var prev = int.Parse(selected[i - 1].Original.Split(' ')[^1]);
            var cur = int.Parse(selected[i].Original.Split(' ')[^1]);
            Assert.True(cur > prev, $"expected chronological order, got {prev} then {cur}");
        }

        // Interleaved noise ([RenderSystem]/[Client]/[Filesystem]) must not survive the selector.
        Assert.DoesNotContain(selected, m =>
            m.Original.Contains("frame") || m.Original.Contains("tick") || m.Original.Contains("mount"));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Seed_ThenLiveLine_ArrivesOnce_WithoutReemittingSeedContent()
    {
        const int totalChatLines = 30;
        var sb = new StringBuilder();
        for (var i = 0; i < totalChatLines; i++)
        {
            sb.Append("[RenderSystem] frame ").Append(i).Append('\n');
            sb.Append("[Client] tick ").Append(i).Append('\n');
            sb.Append("[ALL] p").Append(i).Append(": message ").Append(i).Append('\n');
        }
        File.WriteAllText(_path, sb.ToString(), Encoding.UTF8);

        var live = new List<string>();
        IReadOnlyList<string>? seedBatch = null;
        using var tailer = new ConsoleLogTailer(_path, TimeSpan.FromMilliseconds(50), seedTailBytes: ConsoleLogTailer.DefaultSeedTailBytes);
        tailer.SeedRead += (_, lines) => seedBatch = lines; // subscribed BEFORE Start(), same requirement as above
        tailer.LineRead += (_, line) => { lock (live) live.Add(line); };
        tailer.Start();

        Assert.NotNull(seedBatch);

        const string liveLine = "[ALL] pLive: fresh live message";
        File.AppendAllText(_path, liveLine + "\n", Encoding.UTF8);

        await WaitUntil(() => live.Contains(liveLine), TimeSpan.FromSeconds(3));

        Assert.Contains(liveLine, live);
        Assert.Equal(1, live.Count(l => l == liveLine)); // arrives exactly once -- no duplication

        // Seed content must never resurface through LineRead: Start()'s EmitSeed already advanced
        // _lastPosition past the whole window, so live tailing can only see genuinely new bytes.
        var lastSeedLine = $"[ALL] p{totalChatLines - 1}: message {totalChatLines - 1}";
        Assert.DoesNotContain(lastSeedLine, live);
        Assert.DoesNotContain(seedBatch!, l => l == liveLine);
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
