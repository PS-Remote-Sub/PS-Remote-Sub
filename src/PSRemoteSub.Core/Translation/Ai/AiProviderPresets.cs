using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Translation.Ai;

public sealed record AiProviderPreset(string BaseUrl, string Model, bool RequiresKey);

public static class AiProviderPresets
{
    public static AiProviderPreset Get(AiProvider p) => p switch
    {
        AiProvider.Claude => new("https://api.anthropic.com", "claude-haiku-5-5", true),
        AiProvider.OpenAi => new("https://api.openai.com/v1", "gpt-5-mini", true),
        AiProvider.Gemini => new("https://generativelanguage.googleapis.com/v1beta/openai", "gemini-3.5-flash-lite", true),
        AiProvider.Ollama => new("http://localhost:11434/v1", "", false),
        AiProvider.Custom => new("", "", false),
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };
}
