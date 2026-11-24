using System.Net;
using Microsoft.Extensions.Time.Testing;
using PSRemoteSub.Core.Translation;

namespace PSRemoteSub.Core.Tests.Translation;

public class GoogleTranslatorTests
{
    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static StubHttpHandler Routed(Func<HttpResponseMessage> primary, Func<HttpResponseMessage> fallback) =>
        new(r => r.RequestUri!.Host.Contains("clients5") ? fallback() : primary());

    [Fact]
    public async Task Translate_EncodesQuery()
    {
        var stub = StubHttpHandler.Ok(GoogleResponseParsersTests.Fixture("gtx-en-th.json"));
        var t = new GoogleTranslator(new HttpClient(stub), new FakeTimeProvider());
        await t.TranslateAsync("A&B #1 ゲーム", "ja", "th", default);
        var uri = stub.Requests.Single().AbsoluteUri;
        Assert.Contains("q=A%26B%20%231%20%E3%82%B2%E3%83%BC%E3%83%A0", uri);
        Assert.Contains("sl=ja", uri);
        Assert.Contains("tl=th", uri);
        Assert.StartsWith("https://translate.googleapis.com/translate_a/single?client=gtx", uri);
    }

    [Fact]
    public async Task Html_FallsBack()
    {
        var stub = Routed(() => Ok(GoogleResponseParsersTests.Fixture("captcha.html")),
            () => Ok(GoogleResponseParsersTests.Fixture("clients5-en-th.json")));
        var t = new GoogleTranslator(new HttpClient(stub), new FakeTimeProvider());
        Assert.Equal("สวัสดีชาวโลก", await t.TranslateAsync("Hello world", "en", "th", default));
        Assert.Equal(2, stub.Requests.Count);
        Assert.StartsWith("https://clients5.google.com/translate_a/t?client=dict-chrome-ex", stub.Requests[1].AbsoluteUri);
    }

    [Fact]
    public async Task Status429_FallsBack()
    {
        var stub = Routed(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            () => Ok("[\"สวัสดี\"]"));
        var t = new GoogleTranslator(new HttpClient(stub), new FakeTimeProvider());
        Assert.Equal("สวัสดี", await t.TranslateAsync("Hello", "en", "th", default));
    }

    [Fact]
    public async Task Timeout_FallsBack()
    {
        var stub = new StubHttpHandler(r => r.RequestUri!.Host.Contains("clients5")
            ? Ok("[\"สวัสดี\"]")
            : throw new TaskCanceledException("timeout"));
        var t = new GoogleTranslator(new HttpClient(stub), new FakeTimeProvider());
        Assert.Equal("สวัสดี", await t.TranslateAsync("Hello", "en", "th", default));
    }

    [Fact]
    public async Task Cancellation_PropagatesWithoutFallbackOrBackoff()
    {
        var stub = StubHttpHandler.Ok("[[[\"x\",\"a\"]]]");
        var t = new GoogleTranslator(new HttpClient(stub), new FakeTimeProvider());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.TranslateAsync("Hello", "en", "th", cts.Token));
        Assert.True(stub.Requests.Count <= 1); // never the fallback
        var before = stub.Requests.Count;
        Assert.Equal("x", await t.TranslateAsync("Hello", "en", "th", default)); // not paused
        Assert.Equal(before + 1, stub.Requests.Count);
    }

    [Fact]
    public async Task BothFail_BacksOff30s()
    {
        var stub = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var time = new FakeTimeProvider();
        var t = new GoogleTranslator(new HttpClient(stub), time);
        var start = time.GetUtcNow();

        var ex = await Assert.ThrowsAsync<TranslationUnavailableException>(() => t.TranslateAsync("Hi", "en", "th", default));
        Assert.Equal(start.AddSeconds(30), ex.RetryAt);
        Assert.False(string.IsNullOrEmpty(ex.Message));
        Assert.Equal(2, stub.Requests.Count);

        time.Advance(TimeSpan.FromSeconds(10));
        var ex2 = await Assert.ThrowsAsync<TranslationUnavailableException>(() => t.TranslateAsync("Hi", "en", "th", default));
        Assert.Equal(start.AddSeconds(30), ex2.RetryAt);
        Assert.Equal(2, stub.Requests.Count);

        time.Advance(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<TranslationUnavailableException>(() => t.TranslateAsync("Hi", "en", "th", default));
        Assert.Equal(4, stub.Requests.Count);
    }

    [Fact]
    public async Task PrimaryFailing_ThreeTimes_PrefersFallbackFor10Minutes()
    {
        var stub = Routed(() => new HttpResponseMessage(HttpStatusCode.Forbidden), () => Ok("[\"สวัสดี\"]"));
        var time = new FakeTimeProvider();
        var t = new GoogleTranslator(new HttpClient(stub), time);

        for (var i = 0; i < 3; i++)
            Assert.Equal("สวัสดี", await t.TranslateAsync("Hello", "en", "th", default));
        Assert.Equal(6, stub.Requests.Count); // gtx then clients5, three times

        Assert.Equal("สวัสดี", await t.TranslateAsync("Hello", "en", "th", default));
        Assert.Equal(7, stub.Requests.Count);
        Assert.Equal("clients5.google.com", stub.Requests[^1].Host); // clients5 only

        time.Advance(TimeSpan.FromMinutes(9.9));
        await t.TranslateAsync("Hello", "en", "th", default);
        Assert.Equal(8, stub.Requests.Count);
        Assert.Equal("clients5.google.com", stub.Requests[^1].Host);

        time.Advance(TimeSpan.FromMinutes(0.1)); // 10 min after the switch
        await t.TranslateAsync("Hello", "en", "th", default);
        Assert.Equal(10, stub.Requests.Count);
        Assert.Equal("translate.googleapis.com", stub.Requests[^2].Host); // gtx is tried again first
    }

    [Fact]
    public async Task PrimaryRecovering_ResetsTheCount()
    {
        var primaryOk = false;
        var stub = Routed(
            () => primaryOk ? Ok("[[[\"ก\",\"a\"]]]") : new HttpResponseMessage(HttpStatusCode.Forbidden),
            () => Ok("[\"ข\"]"));
        var t = new GoogleTranslator(new HttpClient(stub), new FakeTimeProvider());

        await t.TranslateAsync("a", "en", "th", default);
        await t.TranslateAsync("a", "en", "th", default);
        primaryOk = true;
        Assert.Equal("ก", await t.TranslateAsync("a", "en", "th", default));
        primaryOk = false;
        await t.TranslateAsync("a", "en", "th", default);
        await t.TranslateAsync("a", "en", "th", default);
        var before = stub.Requests.Count;

        await t.TranslateAsync("a", "en", "th", default); // only 2 consecutive failures so far: gtx first
        Assert.Equal("translate.googleapis.com", stub.Requests[before].Host);
    }

    [Fact]
    public async Task PreferringFallback_FallbackFails_TriesPrimary()
    {
        var fallbackOk = true;
        var stub = Routed(() => new HttpResponseMessage(HttpStatusCode.Forbidden),
            () => fallbackOk ? Ok("[\"ข\"]") : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var t = new GoogleTranslator(new HttpClient(stub), new FakeTimeProvider());
        for (var i = 0; i < 3; i++)
            await t.TranslateAsync("a", "en", "th", default);

        fallbackOk = false;
        var before = stub.Requests.Count;
        await Assert.ThrowsAsync<TranslationUnavailableException>(() => t.TranslateAsync("a", "en", "th", default));
        Assert.Equal(["clients5.google.com", "translate.googleapis.com"], stub.Requests.Skip(before).Select(u => u.Host));
    }

    [Fact]
    public async Task Success_ClearsPause()
    {
        var fail = true;
        var stub = new StubHttpHandler(_ => fail
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : Ok("[[[\"ก\",\"a\"]]]"));
        var time = new FakeTimeProvider();
        var t = new GoogleTranslator(new HttpClient(stub), time);
        await Assert.ThrowsAsync<TranslationUnavailableException>(() => t.TranslateAsync("a", "en", "th", default));
        time.Advance(TimeSpan.FromSeconds(30));
        fail = false;
        Assert.Equal("ก", await t.TranslateAsync("a", "en", "th", default));
        await t.TranslateAsync("a", "en", "th", default);
        Assert.Equal(4, stub.Requests.Count);
    }
}
