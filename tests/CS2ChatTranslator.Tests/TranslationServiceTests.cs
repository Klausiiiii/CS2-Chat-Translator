using CS2ChatTranslator.Services;

namespace CS2ChatTranslator.Tests;

public class TranslationServiceTests
{
    [Fact]
    public async Task Translates_AndCachesResult()
    {
        var calls = 0;
        TranslationService.RawTranslate fake = (text, target, ct) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(("hallo", (string?)"en"));
        };
        using var svc = new TranslationService(fake, TimeSpan.Zero);

        var r1 = await svc.TranslateAsync("hello", "de");
        var r2 = await svc.TranslateAsync("hello", "de"); // positive cache hit

        Assert.Equal("hallo", r1.Text);
        Assert.Equal("en", r1.SourceLanguage);
        Assert.False(r1.Failed);
        Assert.Equal("hallo", r2.Text);
        Assert.Equal(1, calls); // second served from cache
    }

    [Fact]
    public async Task SkipsTranslation_WhenSourceEqualsTarget()
    {
        TranslationService.RawTranslate fake = (text, target, ct) =>
            Task.FromResult(("ignored", (string?)"de"));
        using var svc = new TranslationService(fake, TimeSpan.Zero);

        var r = await svc.TranslateAsync("hallo", "de");

        Assert.True(r.Skipped);
        Assert.Equal("hallo", r.Text);          // original, not the translator output
        Assert.Equal("de", r.SourceLanguage);
    }

    [Fact]
    public async Task Coalesces_ConcurrentIdenticalRequests_IntoOneCall()
    {
        var calls = 0;
        var gate = new TaskCompletionSource();
        TranslationService.RawTranslate fake = async (text, target, ct) =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task;            // hold every caller until released
            return ("übersetzt", (string?)"en");
        };
        using var svc = new TranslationService(fake, TimeSpan.Zero);

        var tasks = Enumerable.Range(0, 10).Select(_ => svc.TranslateAsync("gg", "de")).ToArray();
        await Task.Delay(50);           // let all 10 register on the in-flight task
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, calls);         // single-flight: one network round-trip, not ten
        Assert.All(results, r => Assert.Equal("übersetzt", r.Text));
    }

    [Fact]
    public async Task OversizedInput_ReturnsOriginal_WithoutNetworkCall()
    {
        var calls = 0;
        TranslationService.RawTranslate fake = (text, target, ct) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(("nope", (string?)"en"));
        };
        using var svc = new TranslationService(fake, TimeSpan.Zero);

        var big = new string('a', 1000);
        var r = await svc.TranslateAsync(big, "de");

        Assert.Equal(0, calls);         // never forwarded to the network
        Assert.False(r.Failed);
        Assert.Equal(big, r.Text);      // original passed through unchanged
    }

    [Fact]
    public async Task CachesFailure_SuppressesNetworkRetry_WithinTtl()
    {
        var calls = 0;
        TranslationService.RawTranslate fake = (text, target, ct) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("boom");
        };
        using var svc = new TranslationService(fake, TimeSpan.Zero);

        var r1 = await svc.TranslateAsync("x", "de");
        var after1 = calls;
        var r2 = await svc.TranslateAsync("x", "de");
        var after2 = calls;

        Assert.True(r1.Failed);
        Assert.True(r2.Failed);
        Assert.Equal("x", r2.Text);
        Assert.Equal(2, after1);        // first call: attempt0 + attempt1
        Assert.Equal(after1, after2);   // second call served from negative cache (no extra hits)
    }
}
