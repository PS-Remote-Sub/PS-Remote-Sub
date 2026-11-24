using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Translation.Ai;
using Xunit;

namespace PSRemoteSub.Core.Tests.Translation.Ai;

public class CachingAiTranslatorTests
{
    private sealed class Counting(Func<int, string> reply) : IAiTranslator
    {
        public int Calls;
        public Task<string> TranslateAsync(AiTranslationRequest request, CancellationToken ct) =>
            Task.FromResult(reply(++Calls));
    }

    private static AiTranslationRequest R(string text, params AiContextLine[] ctx) =>
        new(text, ctx, "English", "Thai", "Game");

    [Fact]
    public async Task CachingAi_SameTextDifferentContext_HitsCache()
    {
        var inner = new Counting(n => "t" + n);
        var c = new CachingAiTranslator(inner, "s");
        var a = await c.TranslateAsync(R("hi"), CancellationToken.None);
        var b = await c.TranslateAsync(R("hi", new AiContextLine("prev", null)), CancellationToken.None);
        Assert.Equal(a, b);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Cache_DifferentPerLine_Misses()
    {
        var inner = new Counting(n => "t" + n);
        var c = new CachingAiTranslator(inner, "s");
        await c.TranslateAsync(R("hi") with { Brief = new GameBrief("S", "A") }, CancellationToken.None);
        await c.TranslateAsync(R("hi") with { Brief = new GameBrief("S", "B") }, CancellationToken.None);
        await c.TranslateAsync(R("hi") with { Brief = new GameBrief("S", "B") }, CancellationToken.None);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task CachingAi_FailureNotCached()
    {
        var inner = new Counting(n => n == 1 ? throw new AiTranslationException(AiErrorKind.Unavailable, null) : "ok");
        var c = new CachingAiTranslator(inner, "s");
        await Assert.ThrowsAsync<AiTranslationException>(() => c.TranslateAsync(R("hi"), CancellationToken.None));
        Assert.Equal("ok", await c.TranslateAsync(R("hi"), CancellationToken.None));
        Assert.Equal(2, inner.Calls);
    }

    private sealed class Yielding : IAiTranslator
    {
        public async Task<string> TranslateAsync(AiTranslationRequest request, CancellationToken ct)
        {
            await Task.Yield();
            return "t:" + request.Text;
        }
    }

    [Fact]
    public async Task CachingAi_ConcurrentCallers_NoCorruption()
    {
        // The AI lane and background name lookups call the same cache at once.
        var c = new CachingAiTranslator(new Yielding(), "s", capacity: 8);
        for (int round = 0; round < 20; round++)
        {
            var calls = Enumerable.Range(0, 400).Select(i => Task.Run(() => c.TranslateAsync(R("line " + (i % 37)), CancellationToken.None)));
            var results = await Task.WhenAll(calls);
            Assert.All(results, r => Assert.StartsWith("t:line ", r));
        }
    }
}
