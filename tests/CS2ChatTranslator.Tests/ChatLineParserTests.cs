using CS2ChatTranslator.Models;
using CS2ChatTranslator.Services;

namespace CS2ChatTranslator.Tests;

public class ChatLineParserTests
{
    [Fact]
    public void Parses_AllChat_English_NoTimestamp()
    {
        Assert.True(ChatLineParser.TryParse("[ALL] Klaus: hi everyone", out var msg));
        Assert.NotNull(msg);
        Assert.Equal(ChatType.All, msg!.Type);
        Assert.Equal("Klaus", msg.Player);
        Assert.Equal("hi everyone", msg.Original);
        Assert.False(msg.IsDead);
        Assert.Null(msg.Callout);
    }

    [Fact]
    public void Parses_CtTeamChat_English()
    {
        Assert.True(ChatLineParser.TryParse("[CT] K: gogo", out var msg));
        Assert.Equal(ChatType.CT, msg!.Type);
        Assert.Equal("K", msg.Player);
        Assert.Equal("gogo", msg.Original);
    }

    [Fact]
    public void Parses_TTeamChat_English()
    {
        Assert.True(ChatLineParser.TryParse("[T] I: rush b", out var msg));
        Assert.Equal(ChatType.T, msg!.Type);
    }

    [Fact]
    public void Parses_CyrillicPlayerAndMessage()
    {
        Assert.True(ChatLineParser.TryParse("[ALL] Ваня: привет всем", out var msg));
        Assert.Equal("Ваня", msg!.Player);
        Assert.Equal("привет всем", msg.Original);
    }

    [Fact]
    public void Preserves_ColonInMessage()
    {
        Assert.True(ChatLineParser.TryParse("[ALL] Klaus: time is 12:30 soon", out var msg));
        Assert.Equal("Klaus", msg!.Player);
        Assert.Equal("time is 12:30 soon", msg.Original);
    }

    [Fact]
    public void Parses_GermanAllChat_WithTimestamp_AndBidiMark()
    {
        var line = "04/15 21:21:43  [ALLE] SigmaSkibidiNigity\u200E: \u042F \u0442\u0440\u0430\u0445\u043D\u0443\u043B \u0442\u0432\u043E\u044E \u043C\u0430\u0442\u044C";
        Assert.True(ChatLineParser.TryParse(line, out var msg));
        Assert.Equal(ChatType.All, msg!.Type);
        Assert.Equal("SigmaSkibidiNigity", msg.Player);
        Assert.Equal("Я трахнул твою мать", msg.Original);
        Assert.False(msg.IsDead);
        Assert.Null(msg.Callout);
    }

    [Fact]
    public void Parses_GermanTeamChat_WithCallout()
    {
        var line = "04/15 21:21:58  [AT] SigmaSkibidiNigity\u200E\uFE6BObere AT-Seite: \u043D\u0435\u0433\u0440";
        Assert.True(ChatLineParser.TryParse(line, out var msg));
        Assert.Equal(ChatType.CT, msg!.Type);
        Assert.Equal("SigmaSkibidiNigity", msg.Player);
        Assert.Equal("негр", msg.Original);
        Assert.Equal("Obere AT-Seite", msg.Callout);
    }

    [Fact]
    public void Parses_GermanAllChat_DeadSuffix()
    {
        var line = "04/15 21:23:58  [ALLE] SigmaSkibidiNigity\u200E [TOT]: \u043D\u0435\u0433\u0440";
        Assert.True(ChatLineParser.TryParse(line, out var msg));
        Assert.Equal(ChatType.All, msg!.Type);
        Assert.Equal("SigmaSkibidiNigity", msg.Player);
        Assert.True(msg.IsDead);
    }

    [Fact]
    public void Rejects_NonChatLine_SystemTag()
    {
        Assert.False(ChatLineParser.TryParse(
            "04/15 21:20:03 [RenderSystem] Determined driver version for graphics adapter 0", out _));
        Assert.False(ChatLineParser.TryParse(
            "04/15 21:20:13 [Client] CClientSteamContext init ok, logged on = 1", out _));
        Assert.False(ChatLineParser.TryParse(
            "04/15 21:20:10 [Filesystem] Path ID:             File Path:", out _));
    }

    [Fact]
    public void Rejects_NonChatLine_NoBrackets()
    {
        Assert.False(ChatLineParser.TryParse(
            "04/15 21:20:21 SigmaSkibidiNigity nimmt teil.", out _));
        Assert.False(ChatLineParser.TryParse(
            "04/15 21:21:02 ClientPutInServer create new player controller [Miguel]", out _));
    }

    [Fact]
    public void Rejects_EmptyMessage()
    {
        Assert.False(ChatLineParser.TryParse("[ALL] Klaus: ", out _));
        Assert.False(ChatLineParser.TryParse("[ALL] Klaus:", out _));
        Assert.False(ChatLineParser.TryParse("[ALL] Klaus:    ", out _));
    }

    [Fact]
    public void Rejects_EmptyOrNullLine()
    {
        Assert.False(ChatLineParser.TryParse("", out _));
        Assert.False(ChatLineParser.TryParse("   ", out _));
        Assert.False(ChatLineParser.TryParse(null!, out _));
    }

    [Fact]
    public void Rejects_UnknownTypeTag()
    {
        Assert.False(ChatLineParser.TryParse("[FOO] Klaus: hi", out _));
    }

    [Fact]
    public void Trims_TrailingCarriageReturn()
    {
        Assert.True(ChatLineParser.TryParse("[ALL] Klaus: hi\r", out var msg));
        Assert.Equal("hi", msg!.Original);
    }

    [Fact]
    public void ExtractsOnlyChatLines_FromRealLogExcerpt()
    {
        var excerpt = """
            04/15 21:20:03 [RenderSystem] Determined driver version for graphics adapter 0
            04/15 21:20:10 [Filesystem] Path ID:             File Path:
            04/15 21:20:21 SigmaSkibidiNigity nimmt teil.
            04/15 21:21:02 ClientPutInServer create new player controller [Miguel]
            04/15 21:21:43  [ALLE] SigmaSkibidiNigity‎: Я трахнул твою мать
            04/15 21:21:48  [ALLE] SigmaSkibidiNigity‎: Я трахнул твою мать
            04/15 21:21:55  [ALLE] SigmaSkibidiNigity‎: негр
            04/15 21:21:58  [AT] SigmaSkibidiNigity‎﹫Obere AT-Seite: негр
            04/15 21:22:00  [ALLE] SigmaSkibidiNigity‎: негр
            04/15 21:22:41 [InputSystem] Processing SDL events took 5.8ms
            04/15 21:23:58  [ALLE] SigmaSkibidiNigity‎ [TOT]: негр
            """;

        var chatMessages = excerpt
            .Split('\n')
            .Select(l =>
            {
                ChatLineParser.TryParse(l, out var m);
                return m;
            })
            .Where(m => m is not null)
            .ToList();

        Assert.Equal(6, chatMessages.Count);
        Assert.All(chatMessages, m => Assert.Equal("SigmaSkibidiNigity", m!.Player));
        Assert.Equal(1, chatMessages.Count(m => m!.Type == ChatType.CT));
        Assert.Equal(5, chatMessages.Count(m => m!.Type == ChatType.All));
        Assert.Equal(1, chatMessages.Count(m => m!.IsDead));
        Assert.Equal(1, chatMessages.Count(m => m!.Callout == "Obere AT-Seite"));
    }
}
