using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Translation;
using PSRemoteSub.Core.Translation.Ai;

namespace PSRemoteSub.Core.Tests.Translation;

public class GoogleLaneTranslatorTests
{
    private sealed class FakeTranslator : ITranslator
    {
        public List<(string Text, string Sl, string Tl)> Calls { get; } = [];
        public Exception? Throw { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public int Active, MaxActive;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> TranslateAsync(string text, string sl, string tl, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref Active);
            MaxActive = Math.Max(MaxActive, now);
            Calls.Add((text, sl, tl));
            Entered.TrySetResult();
            try
            {
                if (Gate is { } g) await g.Task;
                if (Throw is { } e) throw e;
                return "ผล:" + text;
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }

    private static AiTranslationRequest Req(string? sl = "zh", string? tl = "th", string text = "你好") =>
        new(text, [new AiContextLine("A", "a")], "จีน", "ไทย", "Game", AiRequestKind.Subtitle,
            new GameBrief("s", "p"), sl, tl);

    [Fact]
    public async Task SendsTextOnly_WithGoogleCodes()
    {
        var inner = new FakeTranslator();
        var result = await new GoogleLaneTranslator(inner).TranslateAsync(Req(), default);
        Assert.Equal("ผล:你好", result);
        Assert.Equal([("你好", "zh-CN", "th")], inner.Calls);
    }

    [Fact]
    public async Task Unavailable_MapsToAiUnavailable()
    {
        var inner = new FakeTranslator { Throw = new TranslationUnavailableException("x", DateTimeOffset.UtcNow) };
        var ex = await Assert.ThrowsAsync<AiTranslationException>(() =>
            new GoogleLaneTranslator(inner).TranslateAsync(Req(), default));
        Assert.Equal(AiErrorKind.Unavailable, ex.Kind);
    }

    [Fact]
    public async Task HttpError_MapsToAiUnavailable()
    {
        var inner = new FakeTranslator { Throw = new HttpRequestException("boom") };
        var ex = await Assert.ThrowsAsync<AiTranslationException>(() =>
            new GoogleLaneTranslator(inner).TranslateAsync(Req(), default));
        Assert.Equal(AiErrorKind.Unavailable, ex.Kind);
    }

    [Fact]
    public async Task Timeout_ThrowsUnavailable()
    {
        var inner = new FakeTranslator { Throw = new TaskCanceledException() };
        var ex = await Assert.ThrowsAsync<AiTranslationException>(() =>
            new GoogleLaneTranslator(inner).TranslateAsync(Req(), default));
        Assert.Equal(AiErrorKind.Unavailable, ex.Kind);
    }

    [Fact]
    public async Task CallerCancel_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var inner = new FakeTranslator { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        inner.Throw = new OperationCanceledException(cts.Token);
        var task = new GoogleLaneTranslator(inner).TranslateAsync(Req(), cts.Token);
        await inner.Entered.Task;
        cts.Cancel();
        inner.Gate.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Theory]
    [InlineData(null, "th")]
    [InlineData("zh", null)]
    public async Task MissingCodes_BadResponse(string? sl, string? tl)
    {
        var inner = new FakeTranslator();
        var ex = await Assert.ThrowsAsync<AiTranslationException>(() =>
            new GoogleLaneTranslator(inner).TranslateAsync(Req(sl, tl), default));
        Assert.Equal(AiErrorKind.BadResponse, ex.Kind);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task ConcurrentCalls_AreSerialized()
    {
        var inner = new FakeTranslator { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var lane = new GoogleLaneTranslator(inner);
        var first = lane.TranslateAsync(Req(text: "one"), default);
        await inner.Entered.Task;
        var second = lane.TranslateAsync(Req(text: "two"), default);
        await Task.Delay(100);
        Assert.Single(inner.Calls);
        inner.Gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, inner.Calls.Count);
        Assert.Equal(1, inner.MaxActive);
    }
}
