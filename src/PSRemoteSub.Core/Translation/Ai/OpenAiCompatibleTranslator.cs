using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Translation.Ai;

public sealed record OpenAiCompatibleOptions(bool UseMaxCompletionTokens, double? Temperature, string? ReasoningEffort)
{
    /// <summary>
    /// OpenAI: <c>max_completion_tokens</c>, no temperature, and <c>reasoning_effort</c> only for reasoning models
    /// (<c>gpt-5*</c> → <c>minimal</c>, <c>o&lt;digit&gt;*</c> → <c>low</c>), since others reject the field.
    /// Everyone else (Gemini included): <c>max_tokens</c>, temperature 0.3, no <c>reasoning_effort</c>.
    /// </summary>
    public static OpenAiCompatibleOptions For(AiProvider p, string model) => p switch
    {
        AiProvider.OpenAi => new(true, null, OpenAiReasoningEffort(model)),
        _ => new(false, 0.3, null),
    };

    private static string? OpenAiReasoningEffort(string? model)
    {
        var m = (model ?? "").Trim();
        if (m.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)) return "minimal";
        if (m.Length >= 2 && (m[0] is 'o' or 'O') && char.IsAsciiDigit(m[1])) return "low";
        return null;
    }
}

/// <summary>Chat-completions client for OpenAI, Gemini (OpenAI-compat), Ollama and custom endpoints.</summary>
public sealed class OpenAiCompatibleTranslator(HttpClient http, AiEndpoint endpoint, OpenAiCompatibleOptions options, TimeSpan? timeout = null) : IAiTranslator
{
    /// <summary>Only generated tokens are billed and the prompt asks for one short line; the headroom is for reasoning.</summary>
    private const int MaxOutputTokens = 2048;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);

    public async Task<string> TranslateAsync(AiTranslationRequest request, CancellationToken ct)
    {
        var (system, user) = AiPromptBuilder.Build(request);
        var body = new JsonObject
        {
            ["model"] = endpoint.Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
            [options.UseMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = MaxOutputTokens,
            // One complete reply. Some routers (9router) stream unless told not to; a stream is still read below.
            ["stream"] = false,
        };
        if (options.Temperature is { } t) body["temperature"] = t;
        if (options.ReasoningEffort is { } re) body["reasoning_effort"] = re;

        var url = endpoint.BaseUrl.ToString().TrimEnd('/') + "/chat/completions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(endpoint.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);

        var json = await AiHttp.SendAsync(http, req, _timeout, ct);

        string? content;
        string? finishReason;
        try
        {
            if (json.TrimStart().StartsWith("data:", StringComparison.Ordinal))
            {
                (content, finishReason) = ReadStream(json);
            }
            else
            {
                var choice = JsonNode.Parse(json)?["choices"]?[0];
                content = choice?["message"]?["content"]?.GetValue<string>();
                finishReason = choice?["finish_reason"] is JsonValue fr && fr.TryGetValue<string>(out var reason) ? reason : null;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
        {
            throw new AiTranslationException(AiErrorKind.BadResponse, null, "unexpected response format");
        }

        var cleaned = AiResponseCleaner.Clean(content);
        if (cleaned is null)
        {
            // The output budget ran out (typically inside a reasoning block). No text is logged.
            if (finishReason == "length")
                Log.Warn("AI response truncated");
            throw new AiTranslationException(AiErrorKind.BadResponse, null, finishReason == "length" ? "output token limit reached before any text" : "no text in response",
                unusableAnswer: true);
        }
        if (!AiResponseCleaner.IsPlausible(cleaned, request.Text))
            throw new AiTranslationException(AiErrorKind.BadResponse, null, "response much longer than the line (not a plain translation)",
                unusableAnswer: true);
        if (AiResponseCleaner.HasCommentary(cleaned, request.Text))
            throw new AiTranslationException(AiErrorKind.BadResponse, null, "response has notes besides the translation",
                unusableAnswer: true);
        return cleaned;
    }

    /// <summary>
    /// A server-sent event stream of <c>chat.completion.chunk</c> objects (sent by some routers even without
    /// <c>"stream": true</c>): the <c>delta.content</c> pieces joined, and the last finish reason. Null content when no
    /// piece had text. Throws <see cref="JsonException"/> on a malformed chunk.
    /// </summary>
    private static (string? Content, string? FinishReason) ReadStream(string body)
    {
        var text = new StringBuilder();
        bool any = false;
        string? finishReason = null;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue; // blank separators, "event:" and ":" comment lines
            var payload = line[5..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
                continue;
            var choice = JsonNode.Parse(payload)?["choices"]?[0];
            if (choice?["delta"]?["content"] is JsonValue piece && piece.TryGetValue<string>(out var s))
            {
                text.Append(s);
                any = true;
            }
            if (choice?["finish_reason"] is JsonValue fr && fr.TryGetValue<string>(out var reason))
                finishReason = reason;
        }
        return (any ? text.ToString() : null, finishReason);
    }
}
