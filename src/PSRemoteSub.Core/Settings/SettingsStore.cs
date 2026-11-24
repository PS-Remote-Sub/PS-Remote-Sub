using System.Text.Json;
using System.Text.Json.Serialization;

namespace PSRemoteSub.Core.Settings;

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    public AppSettings Load()
    {
        if (!File.Exists(path)) return Defaults();
        string text = File.ReadAllText(path);
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(text, Options);
            if (s is null) return Defaults();
            s.Normalize();
            return s;
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".bak", overwrite: true);
            return Defaults();
        }
    }

    public void Save(AppSettings s)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Options));
        File.Move(tmp, path, overwrite: true);
    }

    private static AppSettings Defaults()
    {
        var s = new AppSettings();
        s.Normalize();
        return s;
    }
}
