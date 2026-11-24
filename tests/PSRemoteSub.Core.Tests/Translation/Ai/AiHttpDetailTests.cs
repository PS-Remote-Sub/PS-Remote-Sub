using System.Net;
using PSRemoteSub.Core.Translation.Ai;

namespace PSRemoteSub.Core.Tests.Translation.Ai;

public class AiHttpDetailTests
{
    private static async Task<AiTranslationException> SendFails(HttpStatusCode code, string body)
    {
        var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(code) { Content = new StringContent(body) }));
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://example.test/x");
        return await Assert.ThrowsAsync<AiTranslationException>(() => AiHttp.SendAsync(http, req, TimeSpan.FromSeconds(5), default));
    }

    [Fact]
    public async Task AnthropicLowCredit_IsBilling_WithProviderMessage()
    {
        var e = await SendFails(HttpStatusCode.BadRequest,
            "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits.\"}}");
        Assert.Equal(AiErrorKind.Billing, e.Kind);
        Assert.Equal(400, e.StatusCode);
        Assert.StartsWith("Your credit balance is too low", e.ProviderMessage);
        Assert.Equal("AI Billing (400)", e.Message); // the body still never reaches Message
    }

    [Fact]
    public async Task OpenAiInsufficientQuota_IsBilling()
    {
        var e = await SendFails(HttpStatusCode.TooManyRequests,
            "{\"error\":{\"message\":\"You exceeded your current quota, please check your plan and billing details.\",\"type\":\"insufficient_quota\",\"code\":\"insufficient_quota\"}}");
        Assert.Equal(AiErrorKind.Billing, e.Kind);
    }

    [Fact]
    public async Task Plain429_StaysRateLimited() =>
        Assert.Equal(AiErrorKind.RateLimited, (await SendFails(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"Rate limit reached\"}}")).Kind);

    [Fact]
    public async Task GeminiArrayError_MessageExtracted()
    {
        var e = await SendFails(HttpStatusCode.BadRequest, "[{\"error\":{\"code\":400,\"message\":\"Invalid value at 'x'\"}}]");
        Assert.Equal("Invalid value at 'x'", e.ProviderMessage);
    }

    [Fact]
    public async Task NonJsonBody_NoProviderMessage() =>
        Assert.Null((await SendFails(HttpStatusCode.BadGateway, "<html>bad gateway</html>")).ProviderMessage);

    [Fact]
    public async Task LongMessage_IsTrimmedToOneLine()
    {
        var e = await SendFails(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"line1\\nline2 " + new string('x', 500) + "\"}}");
        Assert.DoesNotContain('\n', e.ProviderMessage!);
        Assert.True(e.ProviderMessage!.Length <= 301);
    }
}
