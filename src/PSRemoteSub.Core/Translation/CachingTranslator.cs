namespace PSRemoteSub.Core.Translation;

/// <summary>
/// Caches successful translations (LRU, 500 by default); exceptions pass through uncached.
/// Not thread-safe: call from one background loop at a time.
/// </summary>
public sealed class CachingTranslator(ITranslator inner, int capacity = 500) : ITranslator
{
    private readonly LruCache<string, string> _cache = new(capacity);

    public async Task<string> TranslateAsync(string text, string sourceGoogle, string targetGoogle, CancellationToken ct)
    {
        var key = $"{sourceGoogle}|{targetGoogle}|{text}";
        if (_cache.TryGet(key, out var hit)) return hit;
        var result = await inner.TranslateAsync(text, sourceGoogle, targetGoogle, ct);
        _cache.Set(key, result);
        return result;
    }
}
