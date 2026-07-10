using System.Text;
using CS2ChatTranslator.Models;
using CS2ChatTranslator.Services;

namespace CS2ChatTranslator.Tests;

public class ChatInjectionServiceTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(),
        $"cs2-inject-test-{Guid.NewGuid():N}");

    public ChatInjectionServiceTests() => Directory.CreateDirectory(_tmpDir);

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    [Fact]
    public void ResolveCfgPath_DerivesFromConsoleLogPath()
    {
        var consoleLog = Path.Combine(_tmpDir, "csgo", "console.log");
        var resolved = ChatInjectionService.ResolveCfgPath(consoleLog);
        Assert.Equal(
            Path.Combine(_tmpDir, "csgo", "cfg", "cs2_translator_reply.cfg"),
            resolved);
    }

    [Fact]
    public void ResolveCfgPath_RejectsEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => ChatInjectionService.ResolveCfgPath(""));
    }

    [Fact]
    public void WriteSayCommand_AllChat_UsesSay()
    {
        var cfg = Path.Combine(_tmpDir, "cs2_translator_reply.cfg");
        ChatInjectionService.WriteSayCommand(cfg, "Hallo", ChatType.All);
        var text = File.ReadAllText(cfg);
        Assert.StartsWith("say \"Hallo\"", text);
    }

    [Fact]
    public void WriteSayCommand_TeamChat_UsesSayTeam()
    {
        var cfg = Path.Combine(_tmpDir, "cs2_translator_reply.cfg");
        ChatInjectionService.WriteSayCommand(cfg, "go b", ChatType.T);
        var text = File.ReadAllText(cfg);
        Assert.StartsWith("say_team \"go b\"", text);

        ChatInjectionService.WriteSayCommand(cfg, "rotate", ChatType.CT);
        Assert.StartsWith("say_team \"rotate\"", File.ReadAllText(cfg));
    }

    [Fact]
    public void WriteSayCommand_NeutralizesDoubleQuotes_KeepsSingleSayArgument()
    {
        // Source/Source-2's console tokenizer does NOT honor backslash-escaped quotes:
        // a literal " ALWAYS closes the quoted say argument. So the only safe escaping is to
        // ensure no ASCII double-quote survives inside the body — backslash-escaping (\") is unsafe.
        var cfg = Path.Combine(_tmpDir, "cs2_translator_reply.cfg");
        ChatInjectionService.WriteSayCommand(cfg, "he said \"hi\"", ChatType.All);
        var line = File.ReadAllText(cfg).TrimEnd('\r', '\n');

        Assert.StartsWith("say \"", line);
        Assert.EndsWith("\"", line);
        var inner = line.Substring("say \"".Length, line.Length - "say \"".Length - 1);
        Assert.DoesNotContain('"', inner); // no ASCII " can break out of the say argument
    }

    [Fact]
    public void WriteSayCommand_PreventsConsoleCommandBreakout()
    {
        // A reply (or translation output) carrying a quote + semicolons must NOT be able to
        // close the say string and run the tail as separate console commands (e.g. "; quit").
        var cfg = Path.Combine(_tmpDir, "cs2_translator_reply.cfg");
        ChatInjectionService.WriteSayCommand(cfg, "nice try\" ; quit ;", ChatType.All);
        var text = File.ReadAllText(cfg);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines); // exactly one say command, no injected lines
        var line = lines[0].TrimEnd('\r');
        Assert.StartsWith("say \"", line);
        Assert.EndsWith("\"", line);
        var inner = line.Substring("say \"".Length, line.Length - "say \"".Length - 1);
        Assert.DoesNotContain('"', inner); // the "; quit" stays inside the quoted argument
    }

    [Fact]
    public void WriteSayCommand_EscapesBackslashes()
    {
        var cfg = Path.Combine(_tmpDir, "cs2_translator_reply.cfg");
        ChatInjectionService.WriteSayCommand(cfg, @"path\to\thing", ChatType.All);
        var text = File.ReadAllText(cfg);
        Assert.Contains(@"""path\\to\\thing""", text);
    }

    [Fact]
    public void WriteSayCommand_StripsNewlines()
    {
        var cfg = Path.Combine(_tmpDir, "cs2_translator_reply.cfg");
        ChatInjectionService.WriteSayCommand(cfg, "line1\nline2\r\nline3", ChatType.All);
        var text = File.ReadAllText(cfg);
        Assert.Contains("\"line1 line2 line3\"", text);
        Assert.DoesNotContain("line1\nline2", text);
    }

    [Fact]
    public void WriteSayCommand_PreservesUmlautsAndCyrillic_AsUtf8()
    {
        var cfg = Path.Combine(_tmpDir, "cs2_translator_reply.cfg");
        ChatInjectionService.WriteSayCommand(cfg, "Привет, äöü!", ChatType.All);

        var bytes = File.ReadAllBytes(cfg);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "file must not have UTF-8 BOM");

        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("Привет, äöü!", text);
    }

    [Fact]
    public void WriteSayCommand_OverwritesExisting()
    {
        var cfg = Path.Combine(_tmpDir, "cs2_translator_reply.cfg");
        ChatInjectionService.WriteSayCommand(cfg, "first", ChatType.All);
        ChatInjectionService.WriteSayCommand(cfg, "second", ChatType.All);
        var text = File.ReadAllText(cfg);
        Assert.Contains("\"second\"", text);
        Assert.DoesNotContain("\"first\"", text);
    }

    [Fact]
    public void WriteSayCommand_CreatesCfgDirectoryIfMissing()
    {
        var cfg = Path.Combine(_tmpDir, "csgo", "cfg", "cs2_translator_reply.cfg");
        Assert.False(Directory.Exists(Path.GetDirectoryName(cfg)!));
        ChatInjectionService.WriteSayCommand(cfg, "hi", ChatType.All);
        Assert.True(File.Exists(cfg));
    }
}
