using System.Collections.Concurrent;
using GTranslate.Translators;

namespace CS2ChatTranslator.Services;

public sealed class TranslationService : IDisposable
{
    private const int MaxCacheEntries = 1024;

    private readonly GoogleTranslator2 _translator = new();
    private readonly ConcurrentDictionary<CacheKey, TranslationResult> _cache = new();
    private readonly ConcurrentQueue<CacheKey> _cacheOrder = new();

    public sealed record TranslationResult(string Text, bool Failed, bool Skipped, string? SourceLanguage);

    private readonly record struct CacheKey(string Text, string TargetLanguage);

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string targetLanguage,
        CancellationToken ct = default)
    {
        var key = new CacheKey(text, targetLanguage);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var result = await _translator.TranslateAsync(text, targetLanguage).ConfigureAwait(false);
                var sourceLang = result.SourceLanguage?.ISO6391;

                TranslationResult tr;
                if (!string.IsNullOrEmpty(sourceLang) &&
                    string.Equals(sourceLang, targetLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    tr = new TranslationResult(text, Failed: false, Skipped: true, SourceLanguage: sourceLang);
                }
                else
                {
                    tr = new TranslationResult(result.Translation, Failed: false, Skipped: false, SourceLanguage: sourceLang);
                }

                Remember(key, tr);
                return tr;
            }
            catch when (attempt == 0)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return new TranslationResult(text, Failed: true, Skipped: false, SourceLanguage: null); }
            }
            catch
            {
                return new TranslationResult(text, Failed: true, Skipped: false, SourceLanguage: null);
            }
        }
        return new TranslationResult(text, Failed: true, Skipped: false, SourceLanguage: null);
    }

    private void Remember(CacheKey key, TranslationResult result)
    {
        if (!_cache.TryAdd(key, result)) return;
        _cacheOrder.Enqueue(key);
        while (_cache.Count > MaxCacheEntries && _cacheOrder.TryDequeue(out var old))
        {
            _cache.TryRemove(old, out _);
        }
    }

    public void Dispose() => _translator.Dispose();
}
