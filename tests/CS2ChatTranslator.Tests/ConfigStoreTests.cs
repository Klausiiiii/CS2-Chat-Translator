using CS2ChatTranslator.Models;
using CS2ChatTranslator.Services;

namespace CS2ChatTranslator.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        $"cs2-config-test-{Guid.NewGuid():N}");
    private readonly string _path;

    public ConfigStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_ReturnsRealConfig_WhenFileMomentarilyLocked()
    {
        ConfigStore.Save(new AppConfig { TargetLanguage = "ru", ConsoleLogPath = @"C:\cs\console.log" }, _path);

        // Hold an exclusive lock, release it shortly after — Load must retry, not mistake the
        // transient IOException for 'missing' and silently reset to defaults.
        var locked = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(150); locked.Dispose(); });

        var cfg = ConfigStore.Load(_path);
        release.Wait();

        Assert.Equal("ru", cfg.TargetLanguage);            // real value survived the lock
        Assert.Equal(@"C:\cs\console.log", cfg.ConsoleLogPath);
    }

    [Fact]
    public void Load_ReturnsDefaults_OnMalformedJson()
    {
        File.WriteAllText(_path, "{ this is : not valid json ]");
        var cfg = ConfigStore.Load(_path);
        Assert.Equal("en", cfg.TargetLanguage);
        Assert.Equal("", cfg.ConsoleLogPath);
    }

    [Fact]
    public async Task Save_UnderParallelSaves_NeverThrows_NoLeftoverTemp_ValidFile()
    {
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, 80).Select(i => Task.Run(() =>
        {
            try { ConfigStore.Save(new AppConfig { TargetLanguage = "l" + (i % 5) }, _path); }
            catch (Exception ex) { errors.Add(ex); }
        }));
        await Task.WhenAll(tasks);

        Assert.Empty(errors);                                              // fixed '.tmp' collides; unique names don't
        Assert.True(File.Exists(_path));
        var cfg = ConfigStore.Load(_path);                                 // never truncated/half-written
        Assert.StartsWith("l", cfg.TargetLanguage);
        var leftovers = Directory.GetFiles(_dir, "*.tmp");
        Assert.Empty(leftovers);                                          // every temp cleaned up
    }
}
