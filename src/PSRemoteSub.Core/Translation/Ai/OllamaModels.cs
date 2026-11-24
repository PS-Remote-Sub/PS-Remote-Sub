using System.Text.Json;
using System.Text.Json.Nodes;

namespace PSRemoteSub.Core.Translation.Ai;

public static class OllamaModels
{
    /// <summary>Lists installed model names via <c>GET {base without /v1}/api/tags</c>. Any failure throws Unavailable.</summary>
    public static async Task<IReadOnlyList<string>> ListAsync(HttpClient http, Uri baseUrl, CancellationToken ct)
    {
        var root = baseUrl.ToString().TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root = root[..^3];
        using var req = new HttpRequestMessage(HttpMethod.Get, root.TrimEnd('/') + "/api/tags");

        var json = await AiHttp.SendAsync(http, req, TimeSpan.FromSeconds(10), ct);
        try
        {
            var names = new List<string>();
            if (JsonNode.Parse(json)?["models"] is JsonArray models)
            {
                foreach (var m in models)
                    if (m?["name"]?.GetValue<string>() is { Length: > 0 } name)
                        names.Add(name);
            }
            return names;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new AiTranslationException(AiErrorKind.Unavailable, null);
        }
    }
}
