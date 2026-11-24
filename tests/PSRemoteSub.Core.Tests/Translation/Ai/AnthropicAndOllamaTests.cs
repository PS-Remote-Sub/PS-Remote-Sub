using System.Net;
using System.Text.Json.Nodes;
using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Translation.Ai;
using Xunit;

namespace PSRemoteSub.Core.Tests.Translation.Ai;

public class AnthropicAndOllamaTests
{
    private static readonly AiTranslationRequest Req = new("Where is the key?", [], "English", "Thai", null);
    private const string OkBody = "{\"content\":[{\"type\":\"thinking\",\"thinking\":\"hmm\"},{\"type\":\"text\",\"text\":\"ไปกันเถอะ\"}],\"stop_reason\":\"end_turn\"}";

    private static AnthropicTranslator Make(StubHttpHandler h) =>
        new(new HttpClient(h), new AiEndpoint(new Uri("https://api.anthropic.com"), "k2", "claude-haiku-5-5"));

    private static StubHttpHandler Status(HttpStatusCode c, string body = "{}") =>
        new(_ => new HttpResponseMessage(c) { Content = new StringContent(body) });

    [Fact]
    public async Task Anthropic_SendsHeadersAndBody()
    {
        HttpRequestMessage? seen = null;
        string? body = null;
        var h = new StubHttpHandler(r =>
        {
            seen = r;
            body = r.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OkBody) };
        });
        await Make(h).TranslateAsync(Req, CancellationToken.None);
        Assert.Equal("https://api.anthropic.com/v1/messages", seen!.RequestUri!.ToString());
        Assert.Equal("k2", seen.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", seen.Headers.GetValues("anthropic-version").Single());
        Assert.Null(seen.Headers.Authorization);
        var j = JsonNode.Parse(body!)!;
        Assert.Equal(2048, (int?)j["max_tokens"]);
        // Current Claude models reject sampling parameters ("`temperature` is deprecated for this model").
        Assert.Null(j["temperature"]);
        Assert.Null(j["top_p"]);
        // Thinking is on by default on the 5.x models: keep it short for a one-line translation.
        Assert.Equal("low", (string?)j["output_config"]?["effort"]);
        Assert.Null(j["thinking"]);
        Assert.False(string.IsNullOrEmpty((string?)j["system"]));
        Assert.Equal("user", (string?)j["messages"]![0]!["role"]);
    }

    [Fact]
    public async Task Anthropic_BriefUsesCachedSystemBlock()
    {
        string? body = null;
        var h = new StubHttpHandler(r =>
        {
            body = r.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OkBody) };
        });
        var req = Req with { Brief = new GameBrief("STATIC-PART", "PERLINE-PART") };
        await Make(h).TranslateAsync(req, CancellationToken.None);
        var sys = JsonNode.Parse(body!)!["system"]!.AsArray();
        Assert.Equal(2, sys.Count);
        Assert.Equal("ephemeral", (string?)sys[0]!["cache_control"]?["type"]);
        Assert.Contains("STATIC-PART", (string?)sys[0]!["text"]);
        Assert.Contains("You translate video game subtitles", (string?)sys[0]!["text"]);
        Assert.Contains("PERLINE-PART", (string?)sys[1]!["text"]);
        Assert.Contains("Reply with only", (string?)sys[1]!["text"]);
        Assert.DoesNotContain("PERLINE-PART", (string?)sys[0]!["text"]);
        Assert.Null(sys[1]!["cache_control"]);
    }

    [Theory]
    [InlineData("claude-haiku-5-5", true)]
    [InlineData("claude-sonnet-5-5", true)]
    [InlineData("claude-opus-5", true)]
    [InlineData("Claude-Fable-5-2", true)]
    [InlineData("claude-haiku-4-5", false)] // rejects effort
    [InlineData("claude-3-5-haiku-latest", false)]
    [InlineData("my-proxy-model", false)]
    public async Task Anthropic_EffortOnlyForFiveSeries(string model, bool sendsEffort)
    {
        string? body = null;
        var h = new StubHttpHandler(r =>
        {
            body = r.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OkBody) };
        });
        var t = new AnthropicTranslator(new HttpClient(h), new AiEndpoint(new Uri("https://api.anthropic.com"), "k2", model));
        await t.TranslateAsync(Req, CancellationToken.None);
        var j = JsonNode.Parse(body!)!;
        Assert.Equal(sendsEffort, j["output_config"] is not null);
        Assert.Null(j["temperature"]);
    }

    [Fact]
    public async Task Anthropic_ParsesFirstTextBlock()
    {
        var r = await Make(StubHttpHandler.Ok(OkBody)).TranslateAsync(Req, CancellationToken.None);
        Assert.Equal("ไปกันเถอะ", r);
    }

    [Theory]
    [InlineData(401, AiErrorKind.Auth)]
    [InlineData(529, AiErrorKind.Unavailable)]
    [InlineData(429, AiErrorKind.RateLimited)]
    public async Task Anthropic_StatusMapping(int status, AiErrorKind kind)
    {
        var ex = await Assert.ThrowsAsync<AiTranslationException>(
            () => Make(Status((HttpStatusCode)status)).TranslateAsync(Req, CancellationToken.None));
        Assert.Equal(kind, ex.Kind);
    }

    [Theory]
    [InlineData("https://api.anthropic.com/v1")]
    [InlineData("https://api.anthropic.com/v1/")]
    [InlineData("https://api.anthropic.com/")]
    [InlineData("https://api.anthropic.com/V1")]
    public async Task Anthropic_BaseWithV1_NotDoubled(string baseUrl)
    {
        var h = StubHttpHandler.Ok(OkBody);
        var t = new AnthropicTranslator(new HttpClient(h), new AiEndpoint(new Uri(baseUrl), "k2", "claude-haiku-5-5"));
        await t.TranslateAsync(Req, CancellationToken.None);
        Assert.Equal("https://api.anthropic.com/v1/messages", h.Requests.Single().AbsoluteUri);
    }

    [Fact]
    public async Task Anthropic_ProxyPathKept()
    {
        var h = StubHttpHandler.Ok(OkBody);
        var t = new AnthropicTranslator(new HttpClient(h), new AiEndpoint(new Uri("https://proxy.test/anthropic/v1/"), "k2", "m"));
        await t.TranslateAsync(Req, CancellationToken.None);
        Assert.Equal("https://proxy.test/anthropic/v1/messages", h.Requests.Single().AbsoluteUri);
    }

    [Fact]
    public async Task Anthropic_ImplausibleAnswer_IsBadResponse()
    {
        var runaway = string.Concat(Enumerable.Repeat("ไปกันเถอะ ", 40));
        var body = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = runaway }),
        }.ToJsonString();
        var ex = await Assert.ThrowsAsync<AiTranslationException>(
            () => Make(StubHttpHandler.Ok(body)).TranslateAsync(Req, CancellationToken.None));
        Assert.Equal(AiErrorKind.BadResponse, ex.Kind);
    }

    [Fact]
    public async Task Anthropic_404_IsModelNotFound()
    {
        var ex = await Assert.ThrowsAsync<AiTranslationException>(
            () => Make(Status(HttpStatusCode.NotFound)).TranslateAsync(Req, CancellationToken.None));
        Assert.Equal(AiErrorKind.ModelNotFound, ex.Kind);
    }

    [Fact]
    public async Task Anthropic_EmptyContent_IsBadResponse()
    {
        var ex = await Assert.ThrowsAsync<AiTranslationException>(
            () => Make(StubHttpHandler.Ok("{\"content\":[]}")).TranslateAsync(Req, CancellationToken.None));
        Assert.Equal(AiErrorKind.BadResponse, ex.Kind);
    }

    [Fact]
    public async Task Ollama_ListsModelNames()
    {
        var h = StubHttpHandler.Ok("{\"models\":[{\"name\":\"llama3.2:latest\"},{\"name\":\"qwen3:8b\"}]}");
        var names = await OllamaModels.ListAsync(new HttpClient(h), new Uri("http://localhost:11434/v1"), CancellationToken.None);
        Assert.Equal(["llama3.2:latest", "qwen3:8b"], names);
        Assert.Equal("http://localhost:11434/api/tags", h.Requests.Single().ToString());
    }

    [Fact]
    public async Task Ollama_Unreachable_IsUnavailable()
    {
        var h = new StubHttpHandler(_ => throw new HttpRequestException("refused"));
        var ex = await Assert.ThrowsAsync<AiTranslationException>(
            () => OllamaModels.ListAsync(new HttpClient(h), new Uri("http://localhost:11434/v1/"), CancellationToken.None));
        Assert.Equal(AiErrorKind.Unavailable, ex.Kind);
    }
}
