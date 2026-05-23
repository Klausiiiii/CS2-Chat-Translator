using System.Text;
using CS2ChatTranslator.Models;

namespace CS2ChatTranslator.Services;

public static class ChatInjectionService
{
    public const string CfgFileName = "cs2_translator_reply.cfg";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static string ResolveCfgPath(string consoleLogPath)
    {
        if (string.IsNullOrWhiteSpace(consoleLogPath))
            throw new ArgumentException("consoleLogPath is required", nameof(consoleLogPath));

        var csgoDir = Path.GetDirectoryName(consoleLogPath);
        if (string.IsNullOrWhiteSpace(csgoDir))
            throw new ArgumentException("could not determine csgo directory from path", nameof(consoleLogPath));

        return Path.Combine(csgoDir, "cfg", CfgFileName);
    }

    public static void WriteSayCommand(string cfgPath, string message, ChatType target)
    {
        if (string.IsNullOrWhiteSpace(cfgPath))
            throw new ArgumentException("cfgPath is required", nameof(cfgPath));

        var escaped = EscapeForSay(message);
        var verb = target == ChatType.CT || target == ChatType.T ? "say_team" : "say";
        var contents = $"{verb} \"{escaped}\"" + Environment.NewLine;

        var dir = Path.GetDirectoryName(cfgPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        File.WriteAllText(cfgPath, contents, Utf8NoBom);
    }

    internal static string EscapeForSay(string message)
    {
        if (string.IsNullOrEmpty(message)) return "";

        var sb = new StringBuilder(message.Length);
        var lastWasSpace = false;
        foreach (var c in message)
        {
            if (c == '\\')        { sb.Append("\\\\"); lastWasSpace = false; }
            else if (c == '"')    { sb.Append("\\\""); lastWasSpace = false; }
            else if (c == '\r' || c == '\n' || c == '\t' || c < 0x20)
            {
                if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
            }
            else                  { sb.Append(c); lastWasSpace = c == ' '; }
        }
        return sb.ToString().Trim();
    }
}
