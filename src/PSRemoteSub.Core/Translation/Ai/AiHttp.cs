using System.Net;
using System.Text.Json;

namespace PSRemoteSub.Core.Translation.Ai;

public static class AiHttp
{
    public static AiErrorKind Classify(HttpStatusCode code) => (int)code switch
    {
        401 or 403 => AiErrorKind.Auth,
        404 => AiErrorKind.ModelNotFound,
        429 => AiErrorKind.RateLimited,
        408 or >= 500 => AiErrorKind.Unavailable,
        _ => AiErrorKind.BadResponse,
    };

    /// <summary>
    /// Sends <paramref name="req"/> and returns the response body. A linked token enforces <paramref name="timeout"/>.
    /// Non-success statuses throw <see cref="AiTranslationException"/> per <see cref="Classify"/>, except a 400 whose
    /// body says the API key is invalid (Gemini's way of rejecting a key), which is <see cref="AiErrorKind.Auth"/>.
    /// Connection failures and timeouts throw it as <see cref="AiErrorKind.Unavailable"/>. Caller cancellation
    /// propagates as <see cref="OperationCanceledException"/>. Exception messages never contain the key, request or
    /// response body, and nothing here is logged.
    /// </summary>
    public static async Task<string> SendAsync(HttpClient http, HttpRequestMessage req, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var resp = await http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(resp, cts.Token);
                var kind = Classify(resp.StatusCode);
                if (resp.StatusCode == HttpStatusCode.BadRequest && SaysKeyInvalid(body))
                    kind = AiErrorKind.Auth;
                else if ((int)resp.StatusCode is 400 or 402 or 403 or 429 && SaysNoCredit(body))
                    kind = AiErrorKind.Billing;
                throw new AiTranslationException(kind, (int)resp.StatusCode, ProviderMessage(body));
            }
            return await resp.Content.ReadAsStringAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiTranslationException(AiErrorKind.Unavailable, null);
        }
        catch (HttpRequestException)
        {
            throw new AiTranslationException(AiErrorKind.Unavailable, null);
        }
    }

    private const int MaxProviderMessageLength = 300;

    /// <summary>An error body, or "" when it cannot be read. It is only searched and never logged.</summary>
    private static async Task<string> ReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>A 400 that rejects the key (Gemini: <c>API_KEY_INVALID</c> / "API key not valid").</summary>
    private static bool SaysKeyInvalid(string body) =>
        body.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase)
        || body.Contains("API key not valid", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The account has no credit or quota left (Anthropic: "credit balance is too low"; OpenAI: <c>insufficient_quota</c>).
    /// Waiting does not help, unlike a rate limit.
    /// </summary>
    private static bool SaysNoCredit(string body) =>
        body.Contains("credit balance", StringComparison.OrdinalIgnoreCase)
        || body.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>error.message</c> from <c>{"error":{…}}</c> or Gemini's <c>[{"error":{…}}]</c>, as one trimmed line; null when
    /// the body is not such JSON.
    /// </summary>
    private static string? ProviderMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                root = root[0];
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String)
                return null;
            var text = message.GetString()?.ReplaceLineEndings(" ").Trim();
            if (string.IsNullOrEmpty(text))
                return null;
            return text.Length > MaxProviderMessageLength ? text[..MaxProviderMessageLength] + "…" : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
