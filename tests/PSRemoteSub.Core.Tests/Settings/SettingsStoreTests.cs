using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Input;
using PSRemoteSub.Core.Layout;
using PSRemoteSub.Core.Settings;
using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "psrs-" + Guid.NewGuid().ToString("N"));
    private string PathOf => Path.Combine(_dir, "settings.json");

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Missing_ReturnsDefaults()
    {
        var s = new SettingsStore(PathOf).Load();
        Assert.Single(s.Profiles);
        Assert.Equal("Grand Theft Auto VI", s.Profiles[0].Name);
        Assert.Equal(5000, s.WebPort);
    }

    [Fact]
    public void RoundTrip_PreservesProfileRegionAndHotkeys()
    {
        var store = new SettingsStore(Path.Combine(_dir, "sub", "settings.json"));
        var s = new AppSettings();
        s.Profiles.Add(new GameProfile
        {
            Name = "Game", SourceLang = "ja", FontSize = 32,
            Zones = [new OcrZone { Region = new RegionFraction(0.1, 0.7, 0.8, 0.2), Filter = PreprocessFilter.WhiteText, Placement = OverlayPlacement.Fit }],
        });
        s.ActiveProfile = "Game";
        s.Hotkeys[HotkeyAction.SaveFrame] = "Ctrl+Shift+X";
        store.Save(s);

        var text = File.ReadAllText(Path.Combine(_dir, "sub", "settings.json"));
        Assert.Contains("\"SaveFrame\"", text);
        Assert.Contains("\"WhiteText\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_dir, "sub", "settings.json.tmp")));

        var r = store.Load();
        var g = r.GetActive();
        Assert.Equal("Game", g.Name);
        Assert.Equal(new RegionFraction(0.1, 0.7, 0.8, 0.2), g.Zones[0].Region);
        Assert.Equal("ja", g.SourceLang);
        Assert.Equal(PreprocessFilter.WhiteText, g.Zones[0].Filter);
        Assert.Equal(OverlayPlacement.Fit, g.Zones[0].Placement);
        Assert.Equal("Ctrl+Shift+X", r.Hotkeys[HotkeyAction.SaveFrame]);
        Assert.Null(r.Profiles[0].LegacyRegion);
    }

    [Fact]
    public void Corrupt_ReturnsDefaults_AndKeepsBak()
    {
        File.WriteAllText(PathOf, "{not json");
        var s = new SettingsStore(PathOf).Load();
        Assert.Single(s.Profiles);
        Assert.Equal("{not json", File.ReadAllText(PathOf + ".bak"));
    }

    [Fact]
    public void MissingFields_FilledWithDefaults()
    {
        File.WriteAllText(PathOf, "{\"webPort\":6000}");
        var s = new SettingsStore(PathOf).Load();
        Assert.Equal(6000, s.WebPort);
        Assert.Single(s.Profiles);
        Assert.Equal("Ctrl+Alt+Shift+S", s.Hotkeys[HotkeyAction.SelectRegion]);
        Assert.Equal(5, s.Hotkeys.Count);
    }

    [Fact]
    public void PartialHotkeys_GetMissingActionsFilled()
    {
        File.WriteAllText(PathOf, "{\"hotkeys\":{\"SaveFrame\":\"Ctrl+Alt+G\"},\"profiles\":[]}");
        var s = new SettingsStore(PathOf).Load();
        Assert.Equal("Ctrl+Alt+G", s.Hotkeys[HotkeyAction.SaveFrame]);
        Assert.Equal("Ctrl+Alt+Shift+O", s.Hotkeys[HotkeyAction.ToggleOverlay]);
        Assert.Single(s.Profiles);
    }

    [Fact]
    public void GetActive_UnknownName_FallsBackToFirst()
    {
        var s = new AppSettings { ActiveProfile = "nope" };
        Assert.Same(s.Profiles[0], s.GetActive());
    }

    [Fact]
    public void OldFile_GetsTranslationDefaults()
    {
        File.WriteAllText(PathOf, "{\"webPort\":5000}");
        var t = new SettingsStore(PathOf).Load().Translation;
        Assert.Equal(AiProvider.Claude, t.Provider);
        Assert.Equal(5, t.Providers.Count);
        Assert.Equal("https://api.anthropic.com", t.Providers[AiProvider.Claude].BaseUrl);
        Assert.Equal("claude-haiku-5-5", t.Providers[AiProvider.Claude].Model);
        Assert.Equal(4, t.ContextLines);
    }

    [Fact]
    public void NewFile_ModeGoogle_DefaultProfileUsesGta6()
    {
        var s = new SettingsStore(PathOf).Load(); // no file
        Assert.Equal(TranslationMode.Google, s.Translation.Mode);
        Assert.Equal("gta6", s.GetActive().LibraryGame);
    }

    [Theory]
    [InlineData("Google", TranslationMode.Google)]
    [InlineData("Ai", TranslationMode.Ai)]
    [InlineData("AiOnly", TranslationMode.Ai)]
    public void OldFile_KnownMode_Maps(string mode, TranslationMode expected)
    {
        File.WriteAllText(PathOf, $"{{\"translation\":{{\"mode\":\"{mode}\"}}}}");
        Assert.Equal(expected, new SettingsStore(PathOf).Load().Translation.Mode);
    }

    [Fact] // AI-only era file: no mode, Ollama with URL and model -> AI
    public void OldFile_NoMode_AiSetUp_LoadsAsAi()
    {
        File.WriteAllText(PathOf, "{\"translation\":{\"provider\":\"Ollama\",\"providers\":{\"Ollama\":{\"baseUrl\":\"http://x/v1\",\"model\":\"m\"}}}}");
        Assert.Equal(TranslationMode.Ai, new SettingsStore(PathOf).Load().Translation.Mode);
    }

    [Fact] // no mode, Claude without key -> Google
    public void OldFile_NoMode_NoKey_LoadsAsGoogle()
    {
        File.WriteAllText(PathOf, "{\"translation\":{\"provider\":\"Claude\"}}");
        Assert.Equal(TranslationMode.Google, new SettingsStore(PathOf).Load().Translation.Mode);
    }

    [Fact] // Claude with a stored key -> AI
    public void OldFile_NoMode_WithKey_LoadsAsAi()
    {
        File.WriteAllText(PathOf, "{\"translation\":{\"provider\":\"Claude\",\"providers\":{\"Claude\":{\"apiKeyProtected\":\"enc\"}}}}");
        Assert.Equal(TranslationMode.Ai, new SettingsStore(PathOf).Load().Translation.Mode);
    }

    [Theory]
    [InlineData("\"Unknown\"")]
    [InlineData("3")]
    public void OldFile_UnknownMode_LoadsAndDecides(string raw)
    {
        File.WriteAllText(PathOf, "{\"webPort\":5055,\"translation\":{\"mode\":" + raw + "}}");
        var s = new SettingsStore(PathOf).Load();
        Assert.False(File.Exists(PathOf + ".bak"));
        Assert.Equal(5055, s.WebPort);
        Assert.Equal(TranslationMode.Google, s.Translation.Mode);
    }

    [Fact]
    public void Mode_RoundTrips()
    {
        var store = new SettingsStore(PathOf);
        var s = store.Load();
        s.Translation.Mode = TranslationMode.Ai;
        store.Save(s);
        Assert.Equal(TranslationMode.Ai, store.Load().Translation.Mode);
    }

    [Fact] // version 2 file: only the default profile with no library gets gta6
    public void V2File_DefaultProfileGetsGta6_OthersKept()
    {
        File.WriteAllText(PathOf, "{\"settingsVersion\":2,\"profiles\":[{\"name\":\"ค่าเริ่มต้น\"},{\"name\":\"Other\"},"
            + "{\"name\":\"Picked\",\"libraryGame\":\"x\"}]}");
        var s = new SettingsStore(PathOf).Load();
        Assert.Equal(["gta6", null, "x"], s.Profiles.Select(p => p.LibraryGame));
        Assert.Equal(3, s.SettingsVersion);
    }

    [Fact] // version 3: a default profile set to "none" stays none
    public void V3File_DefaultProfileNone_Kept()
    {
        File.WriteAllText(PathOf, "{\"settingsVersion\":3,\"profiles\":[{\"name\":\"ค่าเริ่มต้น\"}]}");
        Assert.Null(new SettingsStore(PathOf).Load().Profiles[0].LibraryGame);
    }

    [Fact]
    public void CreatedProfile_HasNoLibrary() => Assert.Null(GameProfile.Create("New").LibraryGame);

    [Fact]
    public void OldFile_WithShowPreviousLine_StillLoads()
    {
        // The overlay shows one line only now; the old option is ignored and dropped on the next save.
        File.WriteAllText(PathOf, "{\"profiles\":[{\"name\":\"A\",\"showPreviousLine\":true,\"fontSize\":40}],\"activeProfile\":\"A\"}");
        var store = new SettingsStore(PathOf);
        var s = store.Load();
        Assert.Equal(40, s.GetActive().FontSize);
        Assert.False(File.Exists(PathOf + ".bak"));
        store.Save(s);
        Assert.DoesNotContain("showPreviousLine", File.ReadAllText(PathOf));
    }

    [Fact]
    public void OldFile_NoMinDisplay_Gets0()
    {
        File.WriteAllText(PathOf, "{\"profiles\":[{\"name\":\"A\"}],\"activeProfile\":\"A\"}");
        Assert.Equal(0, new SettingsStore(PathOf).Load().GetActive().MinDisplaySeconds);
    }

    [Theory]
    [InlineData("25", 10)]
    [InlineData("-2", 0)]
    [InlineData("1.5", 1.5)]
    public void MinDisplay_Clamped(string input, double expected)
    {
        File.WriteAllText(PathOf, "{\"profiles\":[{\"name\":\"A\",\"minDisplaySeconds\":" + input + "}],\"activeProfile\":\"A\"}");
        Assert.Equal(expected, new SettingsStore(PathOf).Load().GetActive().MinDisplaySeconds);
    }

    [Fact]
    public void MinDisplay_NaN_Gets0()
    {
        var s = new AppSettings();
        s.Profiles[0].MinDisplaySeconds = double.NaN;
        s.Normalize();
        Assert.Equal(0, s.GetActive().MinDisplaySeconds);
    }

    [Fact]
    public void NewProfile_DefaultsToCustom()
    {
        Assert.Equal(OverlayPlacement.Custom, GameProfile.Create("x").Zones[0].Placement);
        Assert.Equal(OverlayPlacement.Custom, new AppSettings().GetActive().Zones[0].Placement);
    }

    private const string RegionJson = "{\"x\":0.1,\"y\":0.8,\"width\":0.8,\"height\":0.15}";

    private GameProfile LoadProfile(string profileJson)
    {
        File.WriteAllText(PathOf, "{\"profiles\":[{\"name\":\"A\"," + profileJson + "}],\"activeProfile\":\"A\"}");
        var s = new SettingsStore(PathOf).Load();
        Assert.False(File.Exists(PathOf + ".bak")); // loaded, not reset as unreadable
        return s.GetActive();
    }

    private static void AssertRegion(RegionFraction expected, RegionFraction? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.X, actual.X, 9);
        Assert.Equal(expected.Y, actual.Y, 9);
        Assert.Equal(expected.Width, actual.Width, 9);
        Assert.Equal(expected.Height, actual.Height, 9);
    }

    [Fact]
    public void OldFile_Cover_BecomesCustom_CaptionIsRegion()
    {
        var p = LoadProfile("\"placement\":\"Cover\",\"region\":" + RegionJson);
        Assert.Equal(OverlayPlacement.Custom, p.Zones[0].Placement);
        Assert.Equal(new RegionFraction(0.1, 0.8, 0.8, 0.15), p.Zones[0].CaptionRegion);
    }

    [Fact]
    public void OldFile_Above_BecomesCustom_CaptionAboveRegion()
    {
        var p = LoadProfile("\"placement\":\"Above\",\"region\":" + RegionJson);
        Assert.Equal(OverlayPlacement.Custom, p.Zones[0].Placement);
        AssertRegion(new RegionFraction(0.1, 0.65, 0.8, 0.15), p.Zones[0].CaptionRegion);
    }

    [Fact]
    public void OldFile_Above_NearTop_ClampsYAtZero()
    {
        var p = LoadProfile("\"placement\":\"Above\",\"region\":{\"x\":0.1,\"y\":0.05,\"width\":0.8,\"height\":0.15}");
        AssertRegion(new RegionFraction(0.1, 0, 0.8, 0.15), p.Zones[0].CaptionRegion);
    }

    [Fact]
    public void OldFile_Cover_NoRegion_CaptionStaysNull()
    {
        var p = LoadProfile("\"placement\":\"Cover\"");
        Assert.Equal(OverlayPlacement.Custom, p.Zones[0].Placement);
        Assert.Null(p.Zones[0].CaptionRegion);
    }

    [Fact]
    public void OldFile_Cover_KeepsExistingCaptionRegion()
    {
        var p = LoadProfile("\"placement\":\"Cover\",\"region\":" + RegionJson
            + ",\"captionRegion\":{\"x\":0.3,\"y\":0.85,\"width\":0.4,\"height\":0.08}");
        Assert.Equal(new RegionFraction(0.3, 0.85, 0.4, 0.08), p.Zones[0].CaptionRegion);
    }

    [Fact]
    public void OldFile_Fit_StaysFit()
    {
        var p = LoadProfile("\"placement\":\"Fit\",\"region\":" + RegionJson);
        Assert.Equal(OverlayPlacement.Fit, p.Zones[0].Placement);
        Assert.Null(p.Zones[0].CaptionRegion);
    }

    private const string OldHotkeysJson = "{\"hotkeys\":{\"SelectRegion\":\"Ctrl+Alt+S\",\"ToggleOverlay\":\"alt+ctrl+o\","
        + "\"TogglePause\":\"Ctrl+Alt+P\",\"SaveFrame\":\"Ctrl+Shift+X\"}}";

    [Fact]
    public void OldFile_OldDefaultHotkeys_MoveToNew()
    {
        File.WriteAllText(PathOf, OldHotkeysJson);
        var s = new SettingsStore(PathOf).Load();
        Assert.Equal("Ctrl+Alt+Shift+S", s.Hotkeys[HotkeyAction.SelectRegion]);
        Assert.Equal("Ctrl+Alt+Shift+O", s.Hotkeys[HotkeyAction.ToggleOverlay]);
        Assert.Equal("Ctrl+Alt+Shift+P", s.Hotkeys[HotkeyAction.TogglePause]);
        Assert.Equal("Ctrl+Shift+X", s.Hotkeys[HotkeyAction.SaveFrame]);
        Assert.Equal("Ctrl+Alt+Shift+D", s.Hotkeys[HotkeyAction.ToggleRegionFrames]);
        Assert.Equal(AppSettings.CurrentSettingsVersion, s.SettingsVersion);
    }

    [Fact]
    public void HotkeyMove_RunsOnce()
    {
        File.WriteAllText(PathOf, OldHotkeysJson);
        var store = new SettingsStore(PathOf);
        var s = store.Load();
        s.Hotkeys[HotkeyAction.SelectRegion] = "Ctrl+Alt+S";
        store.Save(s);
        Assert.Equal("Ctrl+Alt+S", store.Load().Hotkeys[HotkeyAction.SelectRegion]);
    }

    [Fact]
    public void NewerFile_KeepsItsVersion()
    {
        File.WriteAllText(PathOf, "{\"settingsVersion\":99}");
        var s = new SettingsStore(PathOf).Load();
        Assert.Equal(99, s.SettingsVersion);
    }

    [Fact]
    public void NewSettings_AreCurrentVersion_AndFramesOff()
    {
        var s = new SettingsStore(PathOf).Load();
        Assert.Equal(3, s.SettingsVersion);
        Assert.False(s.ShowRegionFrames);
    }

    [Fact]
    public void RoundTrip_KeepsCaptionRegionAndFrames()
    {
        var store = new SettingsStore(PathOf);
        var s = new AppSettings { ShowRegionFrames = true };
        s.Profiles[0].Zones[0].CaptionRegion = new RegionFraction(0.3, 0.85, 0.4, 0.08);
        store.Save(s);
        var r = store.Load();
        Assert.True(r.ShowRegionFrames);
        Assert.Equal(new RegionFraction(0.3, 0.85, 0.4, 0.08), r.Profiles[0].Zones[0].CaptionRegion);
    }

    [Fact]
    public void GetCaptionRegion_FallsBackToRegion()
    {
        var region = new RegionFraction(0.1, 0.8, 0.8, 0.15);
        var caption = new RegionFraction(0.3, 0.85, 0.4, 0.08);
        Assert.Equal(region, new OcrZone { Region = region }.GetCaptionRegion());
        Assert.Equal(caption, new OcrZone { Region = region, CaptionRegion = caption }.GetCaptionRegion());
        Assert.Null(new OcrZone().GetCaptionRegion());
    }

    [Theory]
    [InlineData(25, 10)]
    [InlineData(-3, 0)]
    public void ContextLines_Clamped(int input, int expected)
    {
        File.WriteAllText(PathOf, "{\"translation\":{\"contextLines\":" + input + "}}");
        Assert.Equal(expected, new SettingsStore(PathOf).Load().Translation.ContextLines);
    }

    [Theory]
    [InlineData(45, 30)]
    [InlineData(0, 1)]
    [InlineData(5, 5)]
    [InlineData(30, 30)]
    public void MaxWaitingLines_Clamped(int input, int expected)
    {
        File.WriteAllText(PathOf, "{\"translation\":{\"maxWaitingLines\":" + input + "}}");
        Assert.Equal(expected, new SettingsStore(PathOf).Load().Translation.MaxWaitingLines);
    }

    [Theory]
    [InlineData(25, 10)]
    [InlineData(0, 1)]
    [InlineData(4, 4)]
    public void ParallelRequests_Clamped(int input, int expected)
    {
        File.WriteAllText(PathOf, "{\"translation\":{\"parallelRequests\":" + input + "}}");
        Assert.Equal(expected, new SettingsStore(PathOf).Load().Translation.ParallelRequests);
    }

    [Fact]
    public void ParallelRequests_DefaultsToOne() => Assert.Equal(1, new TranslationSettings().ParallelRequests);

    [Fact]
    public void MaxWaitingLines_DefaultsToThree() => Assert.Equal(3, new TranslationSettings().MaxWaitingLines);

    [Fact]
    public void RoundTrip_KeepsProtectedKeyAndGameTitle()
    {
        var store = new SettingsStore(PathOf);
        var s = new AppSettings();
        s.Profiles[0].GameTitle = "Hades";
        s.Translation.Provider = AiProvider.OpenAi;
        s.Normalize();
        s.Translation.Providers[AiProvider.OpenAi].ApiKeyProtected = "enc123";
        store.Save(s);

        var back = store.Load();
        Assert.Equal("Hades", back.Profiles[0].GameTitle);
        Assert.Equal(AiProvider.OpenAi, back.Translation.Provider);
        Assert.Equal("enc123", back.Translation.Providers[AiProvider.OpenAi].ApiKeyProtected);
        Assert.Contains("\"OpenAi\"", File.ReadAllText(PathOf));
    }

    [Fact]
    public void Profile_LibraryGame_RoundTrips()
    {
        var store = new SettingsStore(PathOf);
        var s = new AppSettings();
        s.Profiles[0].LibraryGame = "gta6";
        store.Save(s);

        Assert.Equal("gta6", store.Load().GetActive().LibraryGame);
    }

    [Fact]
    public void OldFile_NoLibraryGame_LoadsAsNull()
    {
        Assert.Null(LoadProfile("\"gameTitle\":\"Hades\"").LibraryGame);
    }

    [Fact]
    public void Profile_SpeakerLinesOnly_RoundTrips_OldFileIsOff()
    {
        var store = new SettingsStore(PathOf);
        var s = new AppSettings();
        s.Profiles[0].Zones[0].SpeakerLinesOnly = true;
        store.Save(s);

        Assert.True(store.Load().GetActive().Zones[0].SpeakerLinesOnly);
        Assert.False(LoadProfile("\"gameTitle\":\"Hades\"").Zones[0].SpeakerLinesOnly);
    }

    [Fact]
    public void RoundTrip_KeepsSpeakers()
    {
        var store = new SettingsStore(PathOf);
        var s = new AppSettings();
        s.Profiles[0].Speakers["Lucia"] = new SpeakerInfo { Thai = "ลูเซีย", Color = "#E070C0" };
        store.Save(s);

        var info = store.Load().GetActive().Speakers["Lucia"];
        Assert.Equal(("ลูเซีย", "#E070C0"), (info.Thai, info.Color));
    }

    [Fact]
    public void OldFile_NoSpeakers_Empty()
    {
        File.WriteAllText(PathOf, "{\"profiles\":[{\"name\":\"A\"}],\"activeProfile\":\"A\"}");
        Assert.Empty(new SettingsStore(PathOf).Load().GetActive().Speakers);
    }

    [Fact]
    public void Speakers_DifferOnlyInCase_MergedCaseInsensitive()
    {
        File.WriteAllText(PathOf, "{\"profiles\":[{\"name\":\"A\",\"speakers\":{\"Lucia\":{\"color\":\"#E070C0\"},"
            + "\"LUCIA\":{\"thai\":\"ลูเซีย\"}}}],\"activeProfile\":\"A\"}");
        var speakers = new SettingsStore(PathOf).Load().GetActive().Speakers;

        var info = Assert.Single(speakers).Value;
        Assert.Equal(("ลูเซีย", "#E070C0"), (info.Thai, info.Color));
        Assert.True(speakers.ContainsKey("lucia"));
    }

    private const string V1ProfileJson = "{\"settingsVersion\":1,\"profiles\":[{\"name\":\"A\","
        + "\"region\":{\"x\":0.1,\"y\":0.8,\"width\":0.8,\"height\":0.1},"
        + "\"captionRegion\":{\"x\":0.1,\"y\":0.6,\"width\":0.8,\"height\":0.1},"
        + "\"filter\":\"WhiteText\",\"placement\":\"Fit\",\"speakerLinesOnly\":true}],\"activeProfile\":\"A\"}";

    [Fact]
    public void Version1File_MovesBoxesIntoOneDialogueZone()
    {
        File.WriteAllText(PathOf, V1ProfileJson);
        var s = new SettingsStore(PathOf).Load();
        Assert.False(File.Exists(PathOf + ".bak"));
        var p = s.GetActive();

        var z = Assert.Single(p.Zones);
        Assert.Equal("บทพูด", z.Name);
        AssertRegion(new RegionFraction(0.1, 0.8, 0.8, 0.1), z.Region);
        AssertRegion(new RegionFraction(0.1, 0.6, 0.8, 0.1), z.CaptionRegion);
        Assert.Equal(PreprocessFilter.WhiteText, z.Filter);
        Assert.Equal(OverlayPlacement.Fit, z.Placement);
        Assert.True(z.SpeakerLinesOnly);
        Assert.True(z.SendToWeb && z.LearnToLibrary);
        Assert.Equal(3, s.SettingsVersion);
        Assert.Null(p.LegacyRegion);
        Assert.Null(p.LegacyCaptionRegion);
        Assert.Null(p.LegacyFilter);
        Assert.Null(p.LegacyPlacement);
        Assert.Null(p.LegacySpeakerLinesOnly);
    }

    [Fact]
    public void Version1CoverFile_MigratesPlacementThenZone()
    {
        var p = LoadProfile("\"placement\":\"Cover\",\"region\":" + RegionJson);
        var z = Assert.Single(p.Zones);
        Assert.Equal(OverlayPlacement.Custom, z.Placement);
        Assert.NotNull(z.CaptionRegion);
        Assert.Equal(z.Region, z.CaptionRegion);
    }

    [Fact]
    public void SavedFile_HasNoLegacyFields()
    {
        File.WriteAllText(PathOf, V1ProfileJson);
        var store = new SettingsStore(PathOf);
        store.Save(store.Load());

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(PathOf));
        var profile = doc.RootElement.GetProperty("profiles")[0];
        var names = profile.EnumerateObject().Select(o => o.Name).ToList();
        Assert.Contains("zones", names);
        foreach (var legacy in new[] { "region", "captionRegion", "filter", "placement", "speakerLinesOnly" })
            Assert.DoesNotContain(legacy, names);
    }

    [Fact]
    public void ProfileWithoutZones_GetsOneDialogueZone()
    {
        var p = LoadProfile("\"gameTitle\":\"Hades\"");
        var z = Assert.Single(p.Zones);
        Assert.Equal(OcrZone.DialogueName, z.Name);
        Assert.False(string.IsNullOrEmpty(z.Id));
        Assert.Same(z, p.GetLastZone());
    }

    [Fact]
    public void DuplicateOrBlankZoneIds_AreRenewed()
    {
        var s = new AppSettings();
        s.Profiles[0].Zones = [new OcrZone { Id = "a", Name = "x" }, new OcrZone { Id = "a", Name = "" }, new OcrZone { Id = "", Name = "y" }];
        s.Profiles[0].LastZoneId = "gone";
        s.Normalize();

        var zones = s.Profiles[0].Zones;
        Assert.Equal(3, zones.Select(z => z.Id).Distinct().Count());
        Assert.All(zones, z => Assert.False(string.IsNullOrWhiteSpace(z.Id)));
        Assert.Equal("a", zones[0].Id);
        Assert.Equal("โซน 4", zones[1].Name);
        Assert.Null(s.Profiles[0].LastZoneId);
    }

    [Fact]
    public void NextName_SkipsTakenNames()
    {
        Assert.Equal("โซน 2", OcrZone.NextName([OcrZone.Dialogue()]));
        Assert.Equal("โซน 3", OcrZone.NextName([new OcrZone { Name = "โซน 2" }]));
    }

    [Fact]
    public void HotkeyMove_StillRunsOnlyBelowVersion1()
    {
        File.WriteAllText(PathOf, "{\"settingsVersion\":1,\"hotkeys\":{\"SelectRegion\":\"Ctrl+Alt+S\"}}");
        Assert.Equal("Ctrl+Alt+S", new SettingsStore(PathOf).Load().Hotkeys[HotkeyAction.SelectRegion]);
    }

    [Fact]
    public void RoundTrip_PreservesTvSettings()
    {
        var store = new SettingsStore(PathOf);
        var s = new AppSettings();
        s.Tv = new TvSettings
        {
            Enabled = true, Mode = TvMode.Strip, TextScale = 150, BoxOpacity = 0.5, StripBottom = 20,
            CalibScale = 1.1, CalibOffsetX = 0.05, CalibOffsetY = -0.05, PcId = "abc123", AdbAddress = "192.168.1.9:5555",
        };
        store.Save(s);

        var tv = store.Load().Tv;
        Assert.True(tv.Enabled);
        Assert.Equal(TvMode.Strip, tv.Mode);
        Assert.Equal(150, tv.TextScale);
        Assert.Equal(0.5, tv.BoxOpacity);
        Assert.Equal(20, tv.StripBottom);
        Assert.Equal(1.1, tv.CalibScale);
        Assert.Equal(0.05, tv.CalibOffsetX);
        Assert.Equal(-0.05, tv.CalibOffsetY);
        Assert.Equal("abc123", tv.PcId);
        Assert.Equal("192.168.1.9:5555", tv.AdbAddress);
    }

    [Fact]
    public void MissingTv_FilledWithDefaults_AndPcIdPersists()
    {
        File.WriteAllText(PathOf, "{\"webPort\":5001}");
        var store = new SettingsStore(PathOf);
        var loaded = store.Load();
        var tv = loaded.Tv;
        Assert.False(tv.Enabled);
        Assert.Equal(100, tv.TextScale);
        Assert.False(string.IsNullOrWhiteSpace(tv.PcId));

        store.Save(loaded);
        Assert.Equal(tv.PcId, store.Load().Tv.PcId);
    }

    [Fact]
    public void NullTvOrBlankPcId_IsRepaired()
    {
        File.WriteAllText(PathOf, "{\"tv\":null}");
        Assert.False(string.IsNullOrWhiteSpace(new SettingsStore(PathOf).Load().Tv.PcId));
        File.WriteAllText(PathOf, "{\"tv\":{\"pcId\":\" \"}}");
        Assert.False(string.IsNullOrWhiteSpace(new SettingsStore(PathOf).Load().Tv.PcId));
    }

    [Fact]
    public void ToConfig_ClampsOutOfRange()
    {
        var tv = new TvSettings
        {
            TextScale = 999, BoxOpacity = 5, StripBottom = -3, CalibScale = 9, CalibOffsetX = 1, CalibOffsetY = -1,
        };
        var c = tv.ToConfig("PC", calibrating: true);
        Assert.Equal(200, c.TextScale);
        Assert.Equal(1.0, c.BoxOpacity);
        Assert.Equal(0, c.StripBottom);
        Assert.Equal(1.2, c.Calibration.Scale);
        Assert.Equal(0.1, c.Calibration.OffsetX);
        Assert.Equal(-0.1, c.Calibration.OffsetY);
        Assert.Equal("PC", c.PcName);
        Assert.True(c.Calibrating);
    }
}
