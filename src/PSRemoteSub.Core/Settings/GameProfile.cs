using System.Text.Json.Serialization;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Layout;

namespace PSRemoteSub.Core.Settings;

public sealed class GameProfile
{
    public const string DefaultName = "ค่าเริ่มต้น";

    /// <summary>The library folder the default profile starts with (see <see cref="AppSettings.CurrentSettingsVersion"/> 3).</summary>
    public const string DefaultLibraryGame = "gta6";

    public string Name { get; set; } = DefaultName;

    /// <summary>The OCR zones of this game; at least one once <see cref="AppSettings"/> has normalized the profile.</summary>
    public List<OcrZone> Zones { get; set; } = [];

    /// <summary>The zone last picked or edited; null or unknown means the first zone.</summary>
    public string? LastZoneId { get; set; }

    public string SourceLang { get; set; } = "en";
    public double FontSize { get; set; } = 28;
    public double BackgroundOpacity { get; set; } = 0.75;
    public string FontFamily { get; set; } = "Leelawadee UI";
    public string? GameTitle { get; set; }

    /// <summary>The translation library folder name (for example <c>gta6</c>) used for this game, or null for none.</summary>
    public string? LibraryGame { get; set; }

    public const double DefaultMinDisplaySeconds = 3;
    public const double MinDisplayMax = 10;

    /// <summary>How long each overlay line stays at least, in seconds (0–<see cref="MinDisplayMax"/>); 0 = replaced at once.</summary>
    public double MinDisplaySeconds { get; set; } = DefaultMinDisplaySeconds;

    /// <summary>Speaker names learned in this game: source name → Thai spelling and the game's colour for it.</summary>
    public Dictionary<string, SpeakerInfo> Speakers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static GameProfile Create(string name) => new() { Name = name, Zones = [OcrZone.Dialogue()] };

    public OcrZone? GetZone(string? id) => id is null ? null : Zones.FirstOrDefault(z => z.Id == id);

    public OcrZone GetLastZone() => GetZone(LastZoneId) ?? Zones[0];

    // Read-only from files written before zones; AppSettings.Normalize nulls them so they are never written back.
    [JsonPropertyName("region"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RegionFraction? LegacyRegion { get; set; }

    [JsonPropertyName("captionRegion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RegionFraction? LegacyCaptionRegion { get; set; }

    [JsonPropertyName("filter"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PreprocessFilter? LegacyFilter { get; set; }

    [JsonPropertyName("placement"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OverlayPlacement? LegacyPlacement { get; set; }

    [JsonPropertyName("speakerLinesOnly"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacySpeakerLinesOnly { get; set; }
}
