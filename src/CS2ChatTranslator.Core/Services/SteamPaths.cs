using System.Runtime.InteropServices;

namespace CS2ChatTranslator.Services;

public static class SteamPaths
{
    private const string CsgoSubPath = "steamapps/common/Counter-Strike Global Offensive/game/csgo";

    public static IEnumerable<string> CandidateCsgoDirectories()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return Normalize(@"C:\Program Files (x86)\Steam\" + CsgoSubPath);
            yield return Normalize(@"C:\Program Files\Steam\" + CsgoSubPath);
            yield return Normalize(@"D:\SteamLibrary\" + CsgoSubPath);
            yield return Normalize(@"E:\SteamLibrary\" + CsgoSubPath);
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".local", "share", "Steam", CsgoSubPath);
            yield return Path.Combine(home, ".steam", "steam", CsgoSubPath);
            yield return Path.Combine(home, ".steam", "root", CsgoSubPath);
            yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam", CsgoSubPath);
        }
    }

    public static string? FindExistingCsgoDirectory()
    {
        foreach (var c in CandidateCsgoDirectories())
        {
            if (Directory.Exists(c)) return c;
        }
        return null;
    }

    private static string Normalize(string path) => path.Replace('\\', Path.DirectorySeparatorChar);
}
