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
