using System.Collections.Concurrent;
using GTranslate.Translators;

namespace CS2ChatTranslator.Services;

public sealed class TranslationService : IDisposable
{
    private const int MaxCacheEntries = 1024;
    private const int MaxInputLength = 256;   // CS2 chat is < ~128 chars; never forward more
    private const long NegativeTtlMs = 30_000;

    private readonly ConcurrentDictionary<CacheKey, TranslationResult> _cache = new();
    private readonly ConcurrentQueue<CacheKey> _cacheOrder = new();
    private int _cacheCount;

    // Single-flight: concurrent identical misses share one in-flight task instead of each
    // firing its own round-trip to the rate-limiting keyless endpoint.
    private readonly ConcurrentDictionary<CacheKey, Task<TranslationResult>> _inflight = new();

    // Short-TTL negative cache: a persistently failing/throttled phrase must not re-hit the
    // network (2 attempts + a retry delay) on every reappearance, which would worsen a 429 storm.
    private readonly ConcurrentDictionary<CacheKey, long> _negativeExpiry = new();
    private readonly ConcurrentQueue<CacheKey> _negativeOrder = new();
    private int _negativeCount;

    // Raw translate seam: returns (translation, detected source ISO-639-1) or throws.
    // The public ctor wraps GTranslate; the internal ctor lets tests inject a fake + fast retry.
    internal delegate Task<(string Translation, string? SourceIso)> RawTranslate(
        string text, string target, CancellationToken ct);

    private readonly RawTranslate _translate;
    private readonly IDisposable? _ownedTranslator;
    private readonly TimeSpan _retryDelay;

    public sealed record TranslationResult(string Text, bool Failed, bool Skipped, string? SourceLanguage);

    private readonly record struct CacheKey(string Text, string TargetLanguage);

    public TranslationService()
    {
        var translator = new GoogleTranslator2();
        _ownedTranslator = translator;
        _retryDelay = TimeSpan.FromSeconds(1);
        _translate = async (text, target, _) =>
        {
            var r = await translator.TranslateAsync(text, target).ConfigureAwait(false);
            return (r.Translation, r.SourceLanguage?.ISO6391);
        };
    }

    internal TranslationService(RawTranslate translate, TimeSpan? retryDelay = null)
    {
        _translate = translate;
        _ownedTranslator = null;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
    }

    public Task<TranslationResult> TranslateAsync(
        string text,
        string targetLanguage,
        CancellationToken ct = default)
    {
        var key = new CacheKey(text, targetLanguage);
        if (_cache.TryGetValue(key, out var cached)) return Task.FromResult(cached);

        // Oversized input never reaches the network: pass the original through unchanged.
        if (text.Length > MaxInputLength)
            return Task.FromResult(new TranslationResult(text, Failed: false, Skipped: false, SourceLanguage: null));

        if (_negativeExpiry.TryGetValue(key, out var expiry))
        {
            if (Environment.TickCount64 < expiry)
                return Task.FromResult(new TranslationResult(text, Failed: true, Skipped: false, SourceLanguage: null));
            if (_negativeExpiry.TryRemove(key, out _)) Interlocked.Decrement(ref _negativeCount);
        }

        // ct is intentionally NOT threaded into the shared task: one caller cancelling must not
        // tear down the round-trip every other caller is awaiting.
        return _inflight.GetOrAdd(key, k => RunAsync(k));
    }

    private async Task<TranslationResult> RunAsync(CacheKey key)
    {
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var (translation, sourceLang) =
                        await _translate(key.Text, key.TargetLanguage, CancellationToken.None).ConfigureAwait(false);

                    var tr = (!string.IsNullOrEmpty(sourceLang) &&
                              string.Equals(sourceLang, key.TargetLanguage, StringComparison.OrdinalIgnoreCase))
                        ? new TranslationResult(key.Text, Failed: false, Skipped: true, SourceLanguage: sourceLang)
                        : new TranslationResult(translation, Failed: false, Skipped: false, SourceLanguage: sourceLang);

                    Remember(key, tr);
                    return tr;
                }
                catch when (attempt == 0)
                {
                    await Task.Delay(_retryDelay).ConfigureAwait(false);
                }
                catch
                {
                    break; // second attempt failed -> negative-cache and report failure
                }
            }

            RememberFailure(key);
            return new TranslationResult(key.Text, Failed: true, Skipped: false, SourceLanguage: null);
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    private void Remember(CacheKey key, TranslationResult result)
    {
        if (!_cache.TryAdd(key, result)) return;
        Interlocked.Increment(ref _cacheCount);
        _cacheOrder.Enqueue(key);
        // Bound via a private counter — ConcurrentDictionary.Count would lock every bucket.
        while (Volatile.Read(ref _cacheCount) > MaxCacheEntries && _cacheOrder.TryDequeue(out var old))
        {
            if (_cache.TryRemove(old, out _)) Interlocked.Decrement(ref _cacheCount);
        }
    }

    private void RememberFailure(CacheKey key)
    {
        var expiry = Environment.TickCount64 + NegativeTtlMs;
        if (_negativeExpiry.TryAdd(key, expiry))
        {
            Interlocked.Increment(ref _negativeCount);
            _negativeOrder.Enqueue(key);
        }
        else
        {
            _negativeExpiry[key] = expiry; // refresh TTL on a repeat failure
        }
        while (Volatile.Read(ref _negativeCount) > MaxCacheEntries && _negativeOrder.TryDequeue(out var old))
        {
            if (_negativeExpiry.TryRemove(old, out _)) Interlocked.Decrement(ref _negativeCount);
        }
    }

    public void Dispose() => _ownedTranslator?.Dispose();
}
