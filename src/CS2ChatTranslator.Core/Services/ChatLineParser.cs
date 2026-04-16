using System.Text.RegularExpressions;
using CS2ChatTranslator.Models;

namespace CS2ChatTranslator.Services;

public static class ChatLineParser
{
    private static readonly Dictionary<string, ChatType> TypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ALL"]  = ChatType.All,
        ["ALLE"] = ChatType.All,
        ["ВСЕ"]  = ChatType.All,
        ["TOUS"] = ChatType.All,
        ["TODOS"]= ChatType.All,
        ["TUTTI"]= ChatType.All,

        ["CT"]   = ChatType.CT,
        ["AT"]   = ChatType.CT,

        ["T"]    = ChatType.T,
        ["TE"]   = ChatType.T,
    };

    private static readonly Regex LineRegex = new(
        @"^(?:\d{2}/\d{2}\s\d{2}:\d{2}:\d{2}\s+)?\[(?<type>[^\]]+)\]\s+(?<rest>.+?)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex DeadSuffix = new(
        @"\s*\[(?:TOT|DEAD|MORT|MUERTO|УБИТ|МЕРТВ)\]\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool TryParse(string? line, out ChatMessage? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var cleaned = line.Replace("\u200E", "").Replace("\u200F", "").TrimEnd('\r', '\n', ' ', '\t');

        var m = LineRegex.Match(cleaned);
        if (!m.Success) return false;

        var typeKey = m.Groups["type"].Value.Trim();
        if (!TypeMap.TryGetValue(typeKey, out var chatType)) return false;

        var rest = m.Groups["rest"].Value;

        var colonIdx = rest.IndexOf(": ", StringComparison.Ordinal);
        string playerRaw;
        string msgText;
        if (colonIdx >= 0)
        {
            playerRaw = rest.Substring(0, colonIdx);
            msgText = rest.Substring(colonIdx + 2);
        }
        else if (rest.EndsWith(":"))
        {
            return false;
        }
        else
        {
            return false;
        }

        msgText = msgText.TrimEnd();
        if (msgText.Length == 0) return false;

        var isDead = false;
        var stripped = DeadSuffix.Replace(playerRaw, "");
        if (stripped != playerRaw)
        {
            isDead = true;
            playerRaw = stripped;
        }

        string player;
        string? callout = null;
        var calloutIdx = playerRaw.IndexOf('\uFE6B');
        if (calloutIdx < 0) calloutIdx = playerRaw.IndexOf('@');
        if (calloutIdx > 0)
        {
            player = playerRaw.Substring(0, calloutIdx).Trim();
            callout = playerRaw.Substring(calloutIdx + 1).Trim();
            if (callout.Length == 0) callout = null;
        }
        else
        {
            player = playerRaw.Trim();
        }

        if (player.Length == 0) return false;

        message = new ChatMessage
        {
            Type = chatType,
            Player = player,
            Original = msgText,
            Callout = callout,
            IsDead = isDead
        };
        return true;
    }
}
