using System.Text.Json;
using System.Text.Json.Serialization;

namespace PSRemoteSub.Core.Settings;

/// <summary>The app window theme; <see cref="System"/> follows the OS light/dark setting.</summary>
public enum AppTheme
{
    Dark,
    Light,
    System,
}

/// <summary>
/// Reads the theme by name (case-insensitive) and falls back to <see cref="AppTheme.Dark"/> for anything else, so a
/// bad theme value never makes the whole settings file unreadable.
/// </summary>
internal sealed class AppThemeJsonConverter : JsonConverter<AppTheme>
{
    public override AppTheme Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse<AppTheme>(reader.GetString(), ignoreCase: true, out var theme)
            && Enum.IsDefined(theme))
            return theme;
        reader.Skip();
        return AppTheme.Dark;
    }

    public override void Write(Utf8JsonWriter writer, AppTheme value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
