using System.Text.Json;
using CS2ChatTranslator.Models;

namespace CS2ChatTranslator.Services;

public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CS2ChatTranslator",
        "config.json");

    public static AppConfig Load() => Load(ConfigPath);

    public static void Save(AppConfig config) => Save(config, ConfigPath);

    internal static AppConfig Load(string path)
    {
        if (!File.Exists(path)) return new AppConfig();

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return new AppConfig();
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                return cfg ?? new AppConfig();
            }
            // Corrupt/garbage content is genuinely unrecoverable -> fall back to defaults.
            catch (JsonException)
            {
                return new AppConfig();
            }
            // A transient lock (AV/indexer/another instance mid File.Move) must NOT be mistaken
            // for 'missing' and silently reset valid settings. Retry briefly, then give up.
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                return new AppConfig();
            }
            catch (UnauthorizedAccessException)
            {
                return new AppConfig();
            }
        }
    }

    internal static void Save(AppConfig config, string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        // Unique temp name so concurrent instances (WinForms + Avalonia share one config dir)
        // never collide on a fixed '<path>.tmp'.
        var tmp = path + "." + Path.GetRandomFileName() + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));

            // Concurrent replaces of `path` race: MoveFileEx(REPLACE_EXISTING) can briefly throw
            // 'access denied' while another writer holds the target. Retry so last-writer-wins
            // instead of surfacing a crash into the settings flow. The write stays atomic.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 9)
                {
                    Thread.Sleep(25);
                }
            }
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
