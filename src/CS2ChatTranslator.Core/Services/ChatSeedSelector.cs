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
