using GTranslate.Translators;

namespace CS2ChatTranslator.Services;

public sealed class TranslationService : IDisposable
{
    private readonly GoogleTranslator2 _translator = new();

    public sealed record TranslationResult(string Text, bool Failed, bool Skipped);

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string targetLanguage,
        CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var result = await _translator.TranslateAsync(text, targetLanguage).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(result.SourceLanguage?.ISO6391) &&
                    string.Equals(result.SourceLanguage.ISO6391, targetLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    return new TranslationResult(text, Failed: false, Skipped: true);
                }

                return new TranslationResult(result.Translation, Failed: false, Skipped: false);
            }
            catch when (attempt == 0)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return new TranslationResult(text, Failed: true, Skipped: false); }
            }
            catch
            {
                return new TranslationResult(text, Failed: true, Skipped: false);
            }
        }
        return new TranslationResult(text, Failed: true, Skipped: false);
    }

    public void Dispose() => _translator.Dispose();
}
