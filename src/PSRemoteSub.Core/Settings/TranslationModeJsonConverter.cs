using System.Text.Json;
using System.Text.Json.Serialization;

namespace PSRemoteSub.Core.Settings;

/// <summary>
/// Reads <c>translation.mode</c> without ever throwing: <c>Google</c>, and <c>Ai</c> or the older <c>AiOnly</c>, map to
/// a mode; anything else (unknown text, a number, an object) reads as null so the app decides from the AI setup.
/// </summary>
public sealed class TranslationModeJsonConverter : JsonConverter<TranslationMode?>
{
    public override TranslationMode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (string.Equals(text, "Google", StringComparison.OrdinalIgnoreCase)) return TranslationMode.Google;
            if (string.Equals(text, "Ai", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "AiOnly", StringComparison.OrdinalIgnoreCase)) return TranslationMode.Ai;
            return null;
        }
        reader.Skip(); // no-op for scalars; steps over a whole object or array
        return null;
    }

    public override void Write(Utf8JsonWriter writer, TranslationMode? value, JsonSerializerOptions options)
    {
        if (value is { } mode) writer.WriteStringValue(mode.ToString());
        else writer.WriteNullValue();
    }
}
