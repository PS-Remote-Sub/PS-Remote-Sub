using System.Net;
using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;
using PSRemoteSub.Core.Translation.Ai;
using Xunit;

namespace PSRemoteSub.Core.Tests.Translation.Ai;

public class AiTranslatorFactoryTests
{
    private sealed class FakeProtector(string? unprotected) : ISecretProtector
    {
        public string Protect(string plain) => "P:" + plain;
        public string? Unprotect(string protectedText) => unprotected;
    }

    private static readonly AiTranslationRequest Req = new("Hello", [], "English", "Thai", null);
    private const string ClaudeOk = "{\"content\":[{\"type\":\"text\",\"text\":\"สวัสดี\"}]}";
    private const string OpenAiOk = "{\"choices\":[{\"message\":{\"content\":\"สวัสดี\"}}]}";

    private static TranslationSettings S(AiProvider p, string baseUrl, string model, string? key = null) => new()
    {
        Provider = p,
        Providers = { [p] = new AiProviderSettings { BaseUrl = baseUrl, Model = model, ApiKeyProtected = key } },
    };

    private static (StubHttpHandler Handler, List<HttpRequestMessage> Seen) Capture(string body)
    {
        var list = new List<HttpRequestMessage>();
        var h = new StubHttpHandler(r =>
        {
            list.Add(r);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        return (h, list);
    }

    [Fact]
    public void Factory_OllamaGetsSmallBudget()
    {
        var o = AiTranslatorFactory.Resolve(S(AiProvider.Ollama, "http://localhost:11434/v1", "llama3"), new FakeProtector(null), new HttpClient());
        Assert.Equal(GameBriefBuilder.OllamaBudget, o.BriefBudget);
        Assert.Equal("Ollama/llama3", o.ModelLabel);
        var c = AiTranslatorFactory.Resolve(S(AiProvider.Claude, "https://api.anthropic.com", "claude-haiku-5-5", "p"), new FakeProtector("k"), new HttpClient());
        Assert.Equal(GameBriefBuilder.CloudBudget, c.BriefBudget);
    }

    [Fact]
    public void Factory_FreshSettings_AsksForKey()
    {
        // A fresh install: Claude by default, no key yet. There is no other translator to fall back on.
        var settings = new AppSettings();
        settings.Normalize();
        var r = AiTranslatorFactory.Resolve(settings.Translation, new FakeProtector(null), new HttpClient());
        Assert.Null(r.Translator);
        Assert.Equal("AiKeyNeeded", r.ConfigErrorKey);
    }

    [Fact]
    public async Task Factory_Claude_UsesAnthropic()
    {
        var (h, seen) = Capture(ClaudeOk);
        var r = AiTranslatorFactory.Resolve(S(AiProvider.Claude, "https://api.anthropic.com", "m", "enc"), new FakeProtector("secret"), new HttpClient(h));
        Assert.Null(r.ConfigErrorKey);
        Assert.Equal("สวัสดี", await r.Translator!.TranslateAsync(Req, CancellationToken.None));
        Assert.Equal("https://api.anthropic.com/v1/messages", seen.Single().RequestUri!.ToString());
        Assert.Equal("secret", seen.Single().Headers.GetValues("x-api-key").Single());
    }

    [Fact]
    public void Factory_MissingKey_ReturnsKeyNeeded()
    {
        var r = AiTranslatorFactory.Resolve(S(AiProvider.OpenAi, "https://api.openai.com/v1", "m"), new FakeProtector("x"), new HttpClient());
        Assert.Null(r.Translator);
        Assert.Equal("AiKeyNeeded", r.ConfigErrorKey);
    }

    [Fact]
    public void UnreadableKey_ReturnsKeyNeeded()
    {
        var r = AiTranslatorFactory.Resolve(S(AiProvider.OpenAi, "https://api.openai.com/v1", "m", "enc"), new FakeProtector(null), new HttpClient());
        Assert.Null(r.Translator);
        Assert.Equal("AiKeyNeeded", r.ConfigErrorKey);
    }

    [Fact]
    public async Task Factory_Ollama_NoKeyOk()
    {
        var (h, seen) = Capture(OpenAiOk);
        var r = AiTranslatorFactory.Resolve(S(AiProvider.Ollama, "http://localhost:11434/v1", "llama3"), new FakeProtector(null), new HttpClient(h));
        Assert.Null(r.ConfigErrorKey);
        await r.Translator!.TranslateAsync(Req, CancellationToken.None);
        Assert.Equal("http://localhost:11434/v1/chat/completions", seen.Single().RequestUri!.ToString());
        Assert.Null(seen.Single().Headers.Authorization);
    }

    [Fact]
    public void Factory_EmptyModel_ReturnsNoModel()
    {
        var r = AiTranslatorFactory.Resolve(S(AiProvider.Ollama, "http://localhost:11434/v1", ""), new FakeProtector(null), new HttpClient());
        Assert.Null(r.Translator);
        Assert.Equal("AiNoModel", r.ConfigErrorKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://x.test")]
    public void Factory_BadUrl_ReturnsAiDown(string url)
    {
        var r = AiTranslatorFactory.Resolve(S(AiProvider.Custom, url, "m"), new FakeProtector(null), new HttpClient());
        Assert.Null(r.Translator);
        Assert.Equal("AiDown", r.ConfigErrorKey);
    }

    [Fact]
    public void Factory_MissingProviderSettings_ReturnsAiDown()
    {
        var s = new TranslationSettings { Provider = AiProvider.Claude };
        Assert.Equal("AiDown", AiTranslatorFactory.Resolve(s, new FakeProtector("k"), new HttpClient()).ConfigErrorKey);
    }

    [Fact]
    public async Task Factory_OverrideKey_UsedInsteadOfStored()
    {
        var (h, seen) = Capture(OpenAiOk);
        var r = AiTranslatorFactory.Resolve(S(AiProvider.OpenAi, "https://api.openai.com/v1", "m", "enc"), new FakeProtector("stored"), new HttpClient(h), "typed");
        await r.Translator!.TranslateAsync(Req, CancellationToken.None);
        Assert.Equal("Bearer typed", seen.Single().Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Factory_OverrideKey_WorksWithNoStoredKey()
    {
        var (h, seen) = Capture(OpenAiOk);
        var r = AiTranslatorFactory.Resolve(S(AiProvider.OpenAi, "https://api.openai.com/v1", "m"), new FakeProtector(null), new HttpClient(h), "typed");
        Assert.Null(r.ConfigErrorKey);
        await r.Translator!.TranslateAsync(Req, CancellationToken.None);
        Assert.Equal("Bearer typed", seen.Single().Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Factory_TrimsKey()
    {
        // A pasted key often carries spaces or a newline; sent as-is it would make the header invalid.
        var (h, seen) = Capture(ClaudeOk);
        var r = AiTranslatorFactory.Resolve(S(AiProvider.Claude, "https://api.anthropic.com", "m", "enc"), new FakeProtector("  secret\r\n"), new HttpClient(h));
        Assert.Null(r.ConfigErrorKey);
        await r.Translator!.TranslateAsync(Req, CancellationToken.None);
        Assert.Equal("secret", seen.Single().Headers.GetValues("x-api-key").Single());

        var (h2, seen2) = Capture(OpenAiOk);
        var r2 = AiTranslatorFactory.Resolve(S(AiProvider.OpenAi, "https://api.openai.com/v1", "m"), new FakeProtector(null), new HttpClient(h2), " typed\n");
        await r2.Translator!.TranslateAsync(Req, CancellationToken.None);
        Assert.Equal("Bearer typed", seen2.Single().Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Factory_BlankOverride_FallsBackToStoredKey()
    {
        var (h, seen) = Capture(OpenAiOk);
        var r = AiTranslatorFactory.Resolve(S(AiProvider.OpenAi, "https://api.openai.com/v1", "m", "enc"), new FakeProtector("stored"), new HttpClient(h), "  \n");
        await r.Translator!.TranslateAsync(Req, CancellationToken.None);
        Assert.Equal("Bearer stored", seen.Single().Headers.Authorization!.ToString());
    }

    [Fact]
    public void Factory_WhitespaceKey_ReturnsKeyNeeded()
    {
        var r = AiTranslatorFactory.Resolve(S(AiProvider.OpenAi, "https://api.openai.com/v1", "m", "enc"), new FakeProtector(" \r\n "), new HttpClient(), "   ");
        Assert.Null(r.Translator);
        Assert.Equal("AiKeyNeeded", r.ConfigErrorKey);
    }

    [Fact]
    public async Task Factory_Ollama_WhitespaceKeySendsNoAuth()
    {
        var (h, seen) = Capture(OpenAiOk);
        var r = AiTranslatorFactory.Resolve(S(AiProvider.Ollama, "http://localhost:11434/v1", "llama3", "enc"), new FakeProtector("  "), new HttpClient(h));
        await r.Translator!.TranslateAsync(Req, CancellationToken.None);
        Assert.Null(seen.Single().Headers.Authorization);
    }
}
