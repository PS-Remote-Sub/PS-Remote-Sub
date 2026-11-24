namespace PSRemoteSub.Core.Translation.Ai;

/// <summary>
/// Caches successful AI translations (LRU, 500 by default); exceptions pass through uncached.
/// Context is deliberately not part of the key. <paramref name="scope"/> identifies provider, model and URL.
/// Thread-safe: the AI lane and background name lookups call it at the same time. The lock is never held across the
/// call to <paramref name="inner"/>.
/// </summary>
public sealed class CachingAiTranslator(IAiTranslator inner, string scope, int capacity = 500) : IAiTranslator
{
    private readonly Lock _gate = new();
    private readonly LruCache<string, string> _cache = new(capacity);

    public async Task<string> TranslateAsync(AiTranslationRequest request, CancellationToken ct)
    {
        var key = $"{scope}|{request.Kind}|{request.TargetLanguageName}|{request.GameTitle}|{request.Brief?.PerLine}|{request.Text}";
        lock (_gate)
        {
            if (_cache.TryGet(key, out var hit))
                return hit;
        }
        var result = await inner.TranslateAsync(request, ct);
        lock (_gate)
            _cache.Set(key, result);
        return result;
    }
}
