using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Translation.Ai;

/// <summary>Either a ready translator, or no translator and a string-table key for the config problem.</summary>
public sealed record AiTranslatorResolution(IAiTranslator? Translator, string? ConfigErrorKey,
    string? ModelLabel = null, int BriefBudget = GameBriefBuilder.CloudBudget);

public static class AiTranslatorFactory
{
    /// <param name="overrideApiKey">Used instead of the stored key (Settings Test button).</param>
    public static AiTranslatorResolution Resolve(TranslationSettings s, ISecretProtector protector, HttpClient http, string? overrideApiKey = null)
    {
        if (!s.Providers.TryGetValue(s.Provider, out var ps)
            || !Uri.TryCreate(ps.BaseUrl, UriKind.Absolute, out var baseUrl)
            || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
            return new(null, "AiDown");

        if (string.IsNullOrWhiteSpace(ps.Model)) return new(null, "AiNoModel");

        // Trimmed: a pasted key often carries spaces or a newline, which would make the request header invalid.
        // A key that is blank after trimming counts as missing.
        var key = Clean(overrideApiKey);
        if (key is null && ps.ApiKeyProtected is not null)
            key = Clean(protector.Unprotect(ps.ApiKeyProtected));

        if (AiProviderPresets.Get(s.Provider).RequiresKey && key is null)
            return new(null, "AiKeyNeeded");

        var endpoint = new AiEndpoint(baseUrl, key, ps.Model);
        IAiTranslator inner = s.Provider == AiProvider.Claude
            ? new AnthropicTranslator(http, endpoint)
            : new OpenAiCompatibleTranslator(http, endpoint, OpenAiCompatibleOptions.For(s.Provider, ps.Model));

        return new(new CachingAiTranslator(inner, $"{s.Provider}|{ps.Model}|{ps.BaseUrl}"), null,
            ModelLabel: $"{s.Provider}/{ps.Model}",
            BriefBudget: s.Provider == AiProvider.Ollama ? GameBriefBuilder.OllamaBudget : GameBriefBuilder.CloudBudget);
    }

    private static string? Clean(string? key) => key?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
}
