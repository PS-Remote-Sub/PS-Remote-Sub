using System.Net;
using System.Text.Json.Nodes;
using PSRemoteSub.Core.Settings;
using PSRemoteSub.Core.Translation.Ai;
using Xunit;

namespace PSRemoteSub.Core.Tests.Translation.Ai;

public class OpenAiCompatibleTranslatorTests
{
    private const string OkBody = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\\\"ไปกันเถอะ\\\"\"}}]}";
    private static readonly AiTranslationRequest Req = new("Where is the key?", [], "English", "Thai", null);

    private static OpenAiCompatibleTranslator Make(StubHttpHandler h, AiProvider p = AiProvider.OpenAi, string? key = "k1",
        string baseUrl = "https://api.openai.com/v1", TimeSpan? timeout = null, string model = "m1") =>
        new(new HttpClient(h), new AiEndpoint(new Uri(baseUrl), key, model), OpenAiCompatibleOptions.For(p, model), timeout);

    private static async Task<JsonNode> SentBody(AiProvider p, string model, string baseUrl = "https://api.openai.com/v1")
    {
        string? body = null;
        var h = new StubHttpHandler(r => { body = r.Content!.ReadAsStringAsync().Result; return OkResponse(); });
        await Make(h, p, baseUrl: baseUrl, model: model).TranslateAsync(Req, CancellationToken.None);
        return JsonNode.Parse(body!)!;
    }

    private static StubHttpHandler Status(HttpStatusCode c, string body = "{}") =>
        new(_ => new HttpResponseMessage(c) { Content = new StringContent(body) });

    private static async Task<AiTranslationException> Fails(OpenAiCompatibleTranslator t) =>
        await Assert.ThrowsAsync<AiTranslationException>(() => t.TranslateAsync(Req, CancellationToken.None));

    private static HttpResponseMessage OkResponse() => new(HttpStatusCode.OK) { Content = new StringContent(OkBody) };

    [Fact]
    public async Task SendsBearerAndBody()
    {
        HttpRequestMessage? seen = null;
        string? body = null;
        var h = new StubHttpHandler(r => { seen = r; body = r.Content!.ReadAsStringAsync().Result; return OkResponse(); });
        await Make(h).TranslateAsync(Req, CancellationToken.None);
        Assert.Equal("https://api.openai.com/v1/chat/completions", seen!.RequestUri!.ToString());
        Assert.Equal("Bearer k1", seen.Headers.GetValues("Authorization").Single());
        var j = JsonNode.Parse(body!)!;
        Assert.Equal("m1", (string?)j["model"]);
        var msgs = j["messages"]!.AsArray();
        Assert.Equal(2, msgs.Count);
        Assert.Equal("system", (string?)msgs[0]!["role"]);
        Assert.Equal("user", (string?)msgs[1]!["role"]);
        Assert.Equal(2048, (int?)j["max_completion_tokens"]);
        Assert.Null(j["temperature"]);
        Assert.Null(j["max_tokens"]);
    }

    [Fact]
    public async Task Gemini_SendsMaxTokensTemperature_NoReasoning()
    {
        string? body = null;
        var h = new StubHttpHandler(r => { body = r.Content!.ReadAsStringAsync().Result; return OkResponse(); });
        await Make(h, AiProvider.Gemini, baseUrl: "https://example.test/v1beta/openai/").TranslateAsync(Req, CancellationToken.None);
        var j = JsonNode.Parse(body!)!;
        Assert.Equal(2048, (int?)j["max_tokens"]);
        Assert.Equal(0.3, (double?)j["temperature"]);
        Assert.Null(j["reasoning_effort"]);
        Assert.Null(j["max_completion_tokens"]);
        Assert.Equal("https://example.test/v1beta/openai/chat/completions", h.Requests.Single().ToString());
    }

    [Theory]
    [InlineData("gpt-5-mini")]
    [InlineData("GPT-5")]
    public async Task OpenAi_Gpt5_SendsMinimalReasoning(string model)
    {
        var j = await SentBody(AiProvider.OpenAi, model);
        Assert.Equal("minimal", (string?)j["reasoning_effort"]);
        Assert.Equal(2048, (int?)j["max_completion_tokens"]);
        Assert.Null(j["temperature"]);
    }

    [Theory]
    [InlineData("o3")]
    [InlineData("o4-mini")]
    [InlineData("O1")]
    public async Task OpenAi_O3_SendsLowReasoning(string model)
    {
        var j = await SentBody(AiProvider.OpenAi, model);
        Assert.Equal("low", (string?)j["reasoning_effort"]);
        Assert.Equal(2048, (int?)j["max_completion_tokens"]);
        Assert.Null(j["temperature"]);
    }

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("gpt-4.1-mini")]
    [InlineData("omni-moderation")]
    public async Task OpenAi_Gpt4o_NoReasoning(string model)
    {
        var j = await SentBody(AiProvider.OpenAi, model);
        Assert.Null(j["reasoning_effort"]);
        Assert.Equal(2048, (int?)j["max_completion_tokens"]);
    }

    [Fact]
    public async Task Custom_Gpt5Model_NoReasoning()
    {
        // Only the OpenAI provider gets reasoning_effort; another server may not know the field.
        var j = await SentBody(AiProvider.Custom, "gpt-5-mini", "https://example.test/v1");
        Assert.Null(j["reasoning_effort"]);
    }

    [Theory]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"<think>still thinking about the\"},\"finish_reason\":\"length\"}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":null},\"finish_reason\":\"length\"}]}")]
    public async Task Truncated_IsBadResponse(string body)
    {
        var e = await Fails(Make(StubHttpHandler.Ok(body)));
        Assert.Equal(AiErrorKind.BadResponse, e.Kind);
        Assert.Null(e.StatusCode);
    }

    [Fact]
    public async Task Gemini400ApiKeyInvalid_IsAuth()
    {
        const string body = "[{\"error\":{\"code\":400,\"message\":\"API key not valid. Please pass a valid API key.\","
            + "\"status\":\"INVALID_ARGUMENT\",\"details\":[{\"reason\":\"API_KEY_INVALID\"}]}}]";
        var e = await Fails(Make(Status(HttpStatusCode.BadRequest, body), AiProvider.Gemini));
        Assert.Equal(AiErrorKind.Auth, e.Kind);
        Assert.Equal(400, e.StatusCode);
        Assert.Equal("AI Auth (400)", e.Message); // the body never reaches the exception
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":\"api key not valid\"}}")]
    [InlineData("{\"error\":{\"reason\":\"api_key_invalid\"}}")]
    public async Task Status400_KeyInvalid_IsAuth_CaseInsensitive(string body) =>
        Assert.Equal(AiErrorKind.Auth, (await Fails(Make(Status(HttpStatusCode.BadRequest, body)))).Kind);

    [Fact]
    public async Task Plain400_IsBadResponse()
    {
        var e = await Fails(Make(Status(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Unsupported parameter: temperature\"}}")));
        Assert.Equal(AiErrorKind.BadResponse, e.Kind);
        Assert.Equal(400, e.StatusCode);
    }

    [Fact]
    public async Task Status404_IsModelNotFound()
    {
        var e = await Fails(Make(Status(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"model_not_found\"}}")));
        Assert.Equal(AiErrorKind.ModelNotFound, e.Kind);
        Assert.Equal(404, e.StatusCode);
    }

    [Fact]
    public async Task Implausible_LongAnswer_IsBadResponse()
    {
        var runaway = string.Concat(Enumerable.Repeat("ไปกันเถอะ ", 40));
        var body = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = runaway } }),
        }.ToJsonString();
        var e = await Fails(Make(StubHttpHandler.Ok(body)));
        Assert.Equal(AiErrorKind.BadResponse, e.Kind);
        Assert.True(e.UnusableAnswer);
    }

    [Fact]
    public async Task Commentary_IsUnusableAnswer()
    {
        var body = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject
            {
                ["content"] = "ไปกันเถอะ\nHere's the translation with the line breaks kept as you asked",
            } }),
        }.ToJsonString();
        var e = await Fails(Make(StubHttpHandler.Ok(body)));
        Assert.Equal(AiErrorKind.BadResponse, e.Kind);
        Assert.True(e.UnusableAnswer);
    }

    [Fact]
    public async Task Plain400_IsNotAnUnusableAnswer() =>
        Assert.False((await Fails(Make(Status(HttpStatusCode.BadRequest, "{}")))).UnusableAnswer);

    [Fact]
    public async Task NoKey_NoAuthorizationHeader()
    {
        HttpRequestMessage? seen = null;
        var h = new StubHttpHandler(r => { seen = r; return OkResponse(); });
        await Make(h, AiProvider.Ollama, key: null, baseUrl: "http://localhost:11434/v1").TranslateAsync(Req, CancellationToken.None);
        Assert.False(seen!.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task ParsesChoiceContent() =>
        Assert.Equal("ไปกันเถอะ", await Make(StubHttpHandler.Ok(OkBody)).TranslateAsync(Req, CancellationToken.None));

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AiErrorKind.Auth)]
    [InlineData(HttpStatusCode.TooManyRequests, AiErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AiErrorKind.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, AiErrorKind.BadResponse)]
    public async Task Status_Classified(HttpStatusCode code, AiErrorKind kind)
    {
        var e = await Fails(Make(Status(code)));
        Assert.Equal(kind, e.Kind);
        Assert.Equal((int)code, e.StatusCode);
    }

    [Fact]
    public async Task ConnectionRefused_IsUnavailable()
    {
        var h = new StubHttpHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new HttpRequestException("refused")));
        Assert.Equal(AiErrorKind.Unavailable, (await Fails(Make(h))).Kind);
    }

    [Fact]
    public async Task Timeout_IsUnavailable()
    {
        var h = new StubHttpHandler(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return OkResponse(); });
        Assert.Equal(AiErrorKind.Unavailable, (await Fails(Make(h, timeout: TimeSpan.FromMilliseconds(50)))).Kind);
    }

    [Fact]
    public async Task CallerCancel_ThrowsOperationCanceled()
    {
        var h = new StubHttpHandler(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return OkResponse(); });
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Make(h).TranslateAsync(Req, cts.Token));
    }

    [Theory]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"  \"}}]}")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("not json")]
    public async Task BadBody_IsBadResponse(string body) =>
        Assert.Equal(AiErrorKind.BadResponse, (await Fails(Make(StubHttpHandler.Ok(body)))).Kind);

    [Fact]
    public async Task ExceptionMessage_HasNoKeyOrText()
    {
        var e = await Fails(Make(Status(HttpStatusCode.Unauthorized, "bad key k1 for Where is the key?")));
        Assert.DoesNotContain("k1", e.Message);
        Assert.DoesNotContain("Where is the key?", e.Message);
        Assert.Equal("AI Auth (401)", e.Message);
    }

    [Fact]
    public void Options_For()
    {
        Assert.Equal(new OpenAiCompatibleOptions(true, null, null), OpenAiCompatibleOptions.For(AiProvider.OpenAi, "gpt-4o"));
        Assert.Equal(new OpenAiCompatibleOptions(true, null, "minimal"), OpenAiCompatibleOptions.For(AiProvider.OpenAi, "gpt-5-mini"));
        Assert.Equal(new OpenAiCompatibleOptions(true, null, "low"), OpenAiCompatibleOptions.For(AiProvider.OpenAi, "o3"));
        Assert.Equal(new OpenAiCompatibleOptions(false, 0.3, null), OpenAiCompatibleOptions.For(AiProvider.Gemini, "gemini-3.5-flash-lite"));
        Assert.Equal(new OpenAiCompatibleOptions(false, 0.3, null), OpenAiCompatibleOptions.For(AiProvider.Custom, "gpt-5"));
    }

    [Fact]
    public async Task AsksForOneCompleteReply_StreamFalse()
    {
        var j = await SentBody(AiProvider.Custom, "translate", "http://localhost:20128/v1");
        Assert.False((bool?)j["stream"]);
    }

    // What 9router sends even without "stream": a server-sent event stream of chat.completion.chunk objects.
    private const string StreamBody =
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ที่กุญแจ\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"อยู่ไหน?\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task StreamedReply_ChunksJoined()
    {
        var h = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(StreamBody) });
        Assert.Equal("ที่กุญแจอยู่ไหน?", await Make(h, AiProvider.Custom).TranslateAsync(Req, CancellationToken.None));
    }

    [Fact]
    public async Task StreamedReply_StoppedByLength_NoText_SaysSo()
    {
        const string body = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"hmm\"},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n";
        var h = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        var ex = await Fails(Make(h, AiProvider.Custom));
        Assert.Equal("output token limit reached before any text", ex.ProviderMessage);
    }

    [Fact]
    public async Task StreamedReply_Garbage_UnexpectedFormat()
    {
        var h = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {not json\n\n") });
        Assert.Equal("unexpected response format", (await Fails(Make(h, AiProvider.Custom))).ProviderMessage);
    }
}
