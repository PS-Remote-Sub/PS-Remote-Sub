using System.Text.Json.Serialization;

namespace PSRemoteSub.Core.Settings;

public enum AiProvider { Claude, OpenAi, Gemini, Ollama, Custom }

/// <summary>Which translator the subtitles go to: Google Translate (no setup) or the AI provider chosen in settings.</summary>
public enum TranslationMode { Google, Ai }

public sealed class AiProviderSettings
{
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string? ApiKeyProtected { get; set; }
}

/// <summary>
/// How subtitles are translated: <see cref="Mode"/> picks Google Translate or the AI provider chosen here.
/// </summary>
public sealed class TranslationSettings
{
    public AiProvider Provider { get; set; } = AiProvider.Claude;
    public Dictionary<AiProvider, AiProviderSettings> Providers { get; set; } = new();
    public int ContextLines { get; set; } = 4;

    /// <summary>Most lines waiting for the AI while it translates one; beyond it the oldest waiting line is skipped.</summary>
    public int MaxWaitingLines { get; set; } = 3;

    /// <summary>
    /// Most lines sent to the AI at once; 1 sends one at a time. Answers are still shown in the order of their lines.
    /// </summary>
    public int ParallelRequests { get; set; } = 1;

    /// <summary>
    /// Google or AI. Null only while loading a file without a (readable) mode; <see cref="AppSettings"/> decides it
    /// from <see cref="HasAiSetUp"/> and it is never null afterwards.
    /// </summary>
    [JsonPropertyName("mode")]
    [JsonConverter(typeof(TranslationModeJsonConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TranslationMode? Mode { get; set; }

    /// <summary>
    /// Whether the selected AI provider is ready to use: an absolute http/https address, a model, and a stored key
    /// when the provider needs one. The key is not decrypted.
    /// </summary>
    public bool HasAiSetUp()
    {
        if (Providers is null || !Providers.TryGetValue(Provider, out var ps) || ps is null)
            return false;
        if (!Uri.TryCreate(ps.BaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return false;
        if (string.IsNullOrWhiteSpace(ps.Model))
            return false;
        return !Translation.Ai.AiProviderPresets.Get(Provider).RequiresKey || ps.ApiKeyProtected is not null;
    }
}
