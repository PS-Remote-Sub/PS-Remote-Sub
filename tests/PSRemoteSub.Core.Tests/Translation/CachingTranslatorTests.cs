using PSRemoteSub.Core.Translation;

namespace PSRemoteSub.Core.Tests.Translation;

public class CachingTranslatorTests
{
    private sealed class Counting(Func<string, string> f) : ITranslator
    {
        public int Calls;
        public Task<string> TranslateAsync(string text, string s, string t, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(f(text));
        }
    }

    private sealed class FuncTranslator(Func<string> f) : ITranslator
    {
        public Task<string> TranslateAsync(string text, string s, string t, CancellationToken ct) => Task.FromResult(f());
    }

    [Fact]
    public async Task Cache_HitsSkipInner()
    {
        var inner = new Counting(t => t + "!");
        var c = new CachingTranslator(inner);
        Assert.Equal("a!", await c.TranslateAsync("a", "en", "th", default));
        Assert.Equal("a!", await c.TranslateAsync("a", "en", "th", default));
        Assert.Equal(1, inner.Calls);
        await c.TranslateAsync("a", "ja", "th", default);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Failures_AreNotCached()
    {
        var n = 0;
        var inner = new FuncTranslator(() => ++n == 1 ? throw new TranslationUnavailableException("x", DateTimeOffset.UnixEpoch) : "ok");
        var c = new CachingTranslator(inner);
        await Assert.ThrowsAsync<TranslationUnavailableException>(() => c.TranslateAsync("a", "en", "th", default));
        Assert.Equal("ok", await c.TranslateAsync("a", "en", "th", default));
        Assert.Equal(2, n);
    }
}
