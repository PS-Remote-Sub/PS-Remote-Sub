using PSRemoteSub.Core.Translation.Ai;
using PSRemoteSub.Core.Layout;
using PSRemoteSub.Core.Input;

namespace PSRemoteSub.Core.Settings;

public sealed class AppSettings
{
    public List<GameProfile> Profiles { get; set; } = [GameProfile.CreateDefault()];
    public string ActiveProfile { get; set; } = GameProfile.DefaultName;
    public string TargetLang { get; set; } = "th";
    public int WebPort { get; set; } = 5000;
    public bool OverlayEnabled { get; set; } = true;
    public bool WebEnabled { get; set; } = true;
    public bool KeepAwake { get; set; } = true;

    /// <summary>Whether the debug outlines of the OCR and caption boxes are drawn over Remote Play.</summary>
    public bool ShowRegionFrames { get; set; }

    /// <summary>Whether the picture comes from Remote Play or a capture card.</summary>
    public PictureSource PictureSource { get; set; } = PictureSource.RemotePlay;

    public CaptureSettings Capture { get; set; } = new();

    /// <summary>The app window theme. On the property (not the enum): the store's enum converter would win over a type attribute.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(AppThemeJsonConverter))]
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    public TranslationSettings Translation { get; set; } = new();

    /// <summary>Subtitles on the Android TV.</summary>
    public TvSettings Tv { get; set; } = new();

    /// <summary>False until the first-run setup is finished. True when absent, so existing users never see it.</summary>
    public bool SetupDone { get; set; } = true;

    /// <summary>Defaults for a brand-new install: same as <c>new AppSettings()</c> but the setup is still pending.</summary>
    public static AppSettings CreateForFirstRun() => new() { SetupDone = false, TrayHintShown = false };

    /// <summary>False until the "the app lives in the tray" hint was shown once (after the first-run wizard). True when absent, so existing users never see it.</summary>
    public bool TrayHintShown { get; set; } = true;

    public Dictionary<HotkeyAction, string> Hotkeys { get; set; } = DefaultHotkeys();

    /// <summary>Settings file layout version; 0 when absent (a file from before versions). <see cref="Normalize"/> raises it to the current version, never lowers it.</summary>
    public int SettingsVersion { get; set; }

    /// <summary>
    /// 1: default hotkeys moved from Ctrl+Alt+letter to Ctrl+Alt+Shift+letter. 2: OCR boxes moved into zones.
    /// 3: the default profile uses the <see cref="GameProfile.DefaultLibraryGame"/> library.
    /// </summary>
    public const int CurrentSettingsVersion = 3;

    public static Dictionary<HotkeyAction, string> DefaultHotkeys() => new()
    {
        [HotkeyAction.SelectRegion] = "Ctrl+Alt+Shift+S",
        [HotkeyAction.ToggleOverlay] = "Ctrl+Alt+Shift+O",
        [HotkeyAction.TogglePause] = "Ctrl+Alt+Shift+P",
        [HotkeyAction.SaveFrame] = "Ctrl+Alt+Shift+F",
        [HotkeyAction.ToggleRegionFrames] = "Ctrl+Alt+Shift+D",
    };

    /// <summary>Defaults before version 1; they clash with other Windows programs.</summary>
    private static readonly Dictionary<HotkeyAction, string> OldDefaultHotkeys = new()
    {
        [HotkeyAction.SelectRegion] = "Ctrl+Alt+S",
        [HotkeyAction.ToggleOverlay] = "Ctrl+Alt+O",
        [HotkeyAction.TogglePause] = "Ctrl+Alt+P",
        [HotkeyAction.SaveFrame] = "Ctrl+Alt+F",
    };

    public GameProfile GetActive() =>
        Profiles.FirstOrDefault(p => p.Name == ActiveProfile) ?? Profiles[0];

    /// <summary>Repairs values a hand-edited or partial file can leave null or missing.</summary>
    internal void Normalize()
    {
        Profiles = Profiles is { Count: > 0 } ? [.. Profiles.Where(p => p is not null)] : [];
        if (Profiles.Count == 0) Profiles.Add(GameProfile.CreateDefault());
        Hotkeys ??= [];
        foreach (var (action, text) in DefaultHotkeys())
            if (!Hotkeys.TryGetValue(action, out var v) || string.IsNullOrWhiteSpace(v))
                Hotkeys[action] = text;
        if (SettingsVersion < 1)
        {
            // Once only: a user who types an old default back in later keeps it.
            var defaults = DefaultHotkeys();
            foreach (var (action, old) in OldDefaultHotkeys)
                if (Canonical(Hotkeys[action]) == old)
                    Hotkeys[action] = defaults[action];
        }
        Capture ??= new();
        Capture.Volume = double.IsNaN(Capture.Volume) ? 1.0 : Math.Clamp(Capture.Volume, 0.0, 1.0);
        if (Capture.Mode is { } mode
            && (mode.Width <= 0 || mode.Height <= 0 || !double.IsFinite(mode.Fps) || mode.Fps <= 0))
            Capture.Mode = null;
        Tv ??= new();
        Tv.Normalize();
        Translation ??= new();
        Translation.Providers ??= [];
        foreach (var provider in Enum.GetValues<AiProvider>())
        {
            var preset = AiProviderPresets.Get(provider);
            if (!Translation.Providers.TryGetValue(provider, out var ps) || ps is null)
                Translation.Providers[provider] = new AiProviderSettings { BaseUrl = preset.BaseUrl, Model = preset.Model };
            else if (provider != AiProvider.Custom)
            {
                // A partial entry (for example only a stored key) gets the preset address and model.
                if (string.IsNullOrWhiteSpace(ps.BaseUrl)) ps.BaseUrl = preset.BaseUrl;
                if (string.IsNullOrWhiteSpace(ps.Model)) ps.Model = preset.Model;
            }
        }
        Translation.Mode ??= Translation.HasAiSetUp() ? TranslationMode.Ai : TranslationMode.Google;
        Translation.ContextLines = Math.Clamp(Translation.ContextLines, 0, 10);
        Translation.MaxWaitingLines = Math.Clamp(Translation.MaxWaitingLines, Pipeline.AiRefiner.MinMaxWaiting, Pipeline.AiRefiner.MaxMaxWaiting);
        Translation.ParallelRequests = Math.Clamp(Translation.ParallelRequests, Pipeline.AiRefiner.MinMaxParallel, Pipeline.AiRefiner.MaxMaxParallel);
        foreach (var profile in Profiles)
        {
            profile.Speakers = MergeSpeakers(profile.Speakers);
            profile.MinDisplaySeconds = double.IsNaN(profile.MinDisplaySeconds)
                ? GameProfile.DefaultMinDisplaySeconds
                : Math.Clamp(profile.MinDisplaySeconds, 0, GameProfile.MinDisplayMax);
            MigratePlacement(profile);
            MigrateZones(profile);
        }
        if (SettingsVersion < 3)
        {
            // Once only: a user who later picks "none" for the default profile keeps it.
            var def = Profiles.FirstOrDefault(p => GameProfile.IsDefaultName(p.Name));
            if (def is not null && string.IsNullOrWhiteSpace(def.LibraryGame))
                def.LibraryGame = GameProfile.DefaultLibraryGame;
        }
        SettingsVersion = Math.Max(SettingsVersion, CurrentSettingsVersion); // a newer app's file keeps its version
    }

    /// <summary>Cover and Above hung off the OCR box; they become a caption box where the subtitle used to be drawn.</summary>
    private static void MigratePlacement(GameProfile profile)
    {
        if (profile.LegacyPlacement is not (OverlayPlacement.Cover or OverlayPlacement.Above))
            return;
        if (profile.LegacyCaptionRegion is null && profile.LegacyRegion is { } r)
            profile.LegacyCaptionRegion = profile.LegacyPlacement == OverlayPlacement.Cover ? r : r with { Y = Math.Max(0, r.Y - r.Height) };
        profile.LegacyPlacement = OverlayPlacement.Custom;
    }

    /// <summary>Moves the single-box fields of old files into a dialogue zone and repairs the zone list.</summary>
    private static void MigrateZones(GameProfile profile)
    {
        profile.Zones = profile.Zones is null ? [] : [.. profile.Zones.Where(z => z is not null)];
        if (profile.Zones.Count == 0)
        {
            var zone = OcrZone.Dialogue();
            zone.Region = profile.LegacyRegion;
            zone.CaptionRegion = profile.LegacyCaptionRegion;
            if (profile.LegacyFilter is { } filter) zone.Filter = filter;
            if (profile.LegacyPlacement is { } placement) zone.Placement = placement;
            if (profile.LegacySpeakerLinesOnly is { } speakerOnly) zone.SpeakerLinesOnly = speakerOnly;
            profile.Zones.Add(zone);
        }
        profile.LegacyRegion = null;
        profile.LegacyCaptionRegion = null;
        profile.LegacyFilter = null;
        profile.LegacyPlacement = null;
        profile.LegacySpeakerLinesOnly = null;

        var seen = new HashSet<string>();
        foreach (var zone in profile.Zones)
        {
            if (string.IsNullOrWhiteSpace(zone.Id) || !seen.Add(zone.Id))
            {
                zone.Id = Guid.NewGuid().ToString("N");
                seen.Add(zone.Id);
            }
            zone.TriggerWords ??= "";
            zone.Name ??= "";
        }
        foreach (var zone in profile.Zones.Where(z => string.IsNullOrWhiteSpace(z.Name)))
            zone.Name = OcrZone.NextName(profile.Zones);
        if (profile.LastZoneId is not null && profile.GetZone(profile.LastZoneId) is null)
            profile.LastZoneId = null;
    }

    private static string? Canonical(string? hotkey)
    {
        try
        {
            return HotkeySpec.Parse(hotkey ?? "").ToString();
        }
        catch (FormatException)
        {
            return null; // left alone
        }
    }

    /// <summary>
    /// Speaker names are case-insensitive (OCR may read "LUCIA" for "Lucia"), but JSON loads a case-sensitive dictionary:
    /// rebuild it, merging entries that differ only in case (first non-null value of each field wins).
    /// </summary>
    public static Dictionary<string, SpeakerInfo> MergeSpeakers(IEnumerable<KeyValuePair<string, SpeakerInfo>>? speakers)
    {
        var merged = new Dictionary<string, SpeakerInfo>(StringComparer.OrdinalIgnoreCase);
        if (speakers is null)
            return merged;
        foreach (var (name, info) in speakers)
        {
            if (string.IsNullOrWhiteSpace(name) || info is null)
                continue;
            if (merged.TryGetValue(name, out var existing))
            {
                existing.Thai ??= info.Thai;
                existing.Color ??= info.Color;
            }
            else
            {
                merged[name] = info.Copy();
            }
        }
        return merged;
    }
}
