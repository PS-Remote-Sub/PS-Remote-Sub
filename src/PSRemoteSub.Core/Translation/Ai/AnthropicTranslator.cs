using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PSRemoteSub.Core.Translation.Ai;

/// <summary>Anthropic Messages API client (Claude).</summary>
public sealed class AnthropicTranslator(HttpClient http, AiEndpoint endpoint, TimeSpan? timeout = null) : IAiTranslator
{
    /// <summary>Only generated tokens are billed and the prompt asks for one short line; the headroom is for reasoning.</summary>
    private const int MaxOutputTokens = 2048;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);

    private static readonly string[] FiveSeriesPrefixes =
        ["claude-haiku-5", "claude-sonnet-5", "claude-opus-5", "claude-fable-5", "claude-mythos-5"];

    /// <summary><c>claude-haiku-5-5</c>, <c>claude-opus-5</c>, <c>claude-fable-5-2</c>… (case-insensitive).</summary>
    internal static bool IsFiveSeries(string model)
    {
        var m = model.Trim();
        return FiveSeriesPrefixes.Any(p => m.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string> TranslateAsync(AiTranslationRequest request, CancellationToken ct)
    {
        var (system, user) = AiPromptBuilder.Build(request);
        JsonNode systemNode = system;
        if (request.Kind == AiRequestKind.Subtitle && request.Brief is { } brief)
        {
            // Two blocks: the instructions and the game's static notes are the same for every line, so they are cached.
            var (head, tail) = AiPromptBuilder.BuildSystemParts(request);
            systemNode = new JsonArray(
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = head + AiPromptBuilder.NotesHeader + brief.Static,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
                },
                new JsonObject { ["type"] = "text", ["text"] = brief.PerLine + "\n" + tail });
        }
        // No temperature/top_p/top_k: current Claude models reject sampling parameters with a 400.
        var body = new JsonObject
        {
            ["model"] = endpoint.Model,
            ["max_tokens"] = MaxOutputTokens,
            ["system"] = systemNode,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        // The 5.x models think by default; a one-line subtitle needs little of it, and less thinking is faster.
        // Older models (Haiku 4.5, 3.x) reject `effort`, so send it only where it is known to work.
        if (IsFiveSeries(endpoint.Model))
            body["output_config"] = new JsonObject { ["effort"] = "low" };

        using var req = new HttpRequestMessage(HttpMethod.Post, MessagesUrl(endpoint.BaseUrl))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("anthropic-version", "2023-06-01");
        if (!string.IsNullOrEmpty(endpoint.ApiKey))
            req.Headers.Add("x-api-key", endpoint.ApiKey);

        var json = await AiHttp.SendAsync(http, req, _timeout, ct);

        string? content = null;
        try
        {
            if (JsonNode.Parse(json)?["content"] is JsonArray blocks)
            {
                foreach (var block in blocks)
                {
                    if ((string?)block?["type"] == "text")
                    {
                        content = block["text"]?.GetValue<string>();
                        break;
                    }
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new AiTranslationException(AiErrorKind.BadResponse, null, "unexpected response format");
        }

        var cleaned = AiResponseCleaner.Clean(content)
            ?? throw new AiTranslationException(AiErrorKind.BadResponse, null, "no text in response", unusableAnswer: true);
        if (!AiResponseCleaner.IsPlausible(cleaned, request.Text))
            throw new AiTranslationException(AiErrorKind.BadResponse, null, "response much longer than the line (not a plain translation)",
                unusableAnswer: true);
        if (AiResponseCleaner.HasCommentary(cleaned, request.Text))
            throw new AiTranslationException(AiErrorKind.BadResponse, null, "response has notes besides the translation",
                unusableAnswer: true);
        return cleaned;
    }

    /// <summary><c>{base}/v1/messages</c>; a base that already ends in <c>/v1</c> (a common paste) is not doubled.</summary>
    internal static string MessagesUrl(Uri baseUrl)
    {
        var root = baseUrl.AbsoluteUri.TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            root = root[..^3];
        return new Uri(root + "/v1/messages").AbsoluteUri;
    }
}
