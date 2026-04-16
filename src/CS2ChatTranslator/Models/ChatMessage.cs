namespace CS2ChatTranslator.Models;

public sealed class ChatMessage
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public ChatType Type { get; init; }
    public string Player { get; init; } = "";
    public string Original { get; init; } = "";
    public string? Callout { get; init; }
    public bool IsDead { get; init; }
    public string? Translation { get; set; }
    public bool TranslationFailed { get; set; }
}
