using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Tests.Settings;

public sealed class SetupFlagTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "psrs-" + Guid.NewGuid().ToString("N"));
    private string PathOf => Path.Combine(_dir, "settings.json");

    public SetupFlagTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void SetupDone_DefaultsToTrue() => Assert.True(new AppSettings().SetupDone);

    [Fact]
    public void SetupDone_MissingInFile_LoadsTrue()
    {
        File.WriteAllText(PathOf, """{"settingsVersion":3}""");
        Assert.True(new SettingsStore(PathOf).Load().SetupDone);
    }

    [Fact]
    public void CreateForFirstRun_IsNotDone_AndRoundTrips()
    {
        var first = AppSettings.CreateForFirstRun();
        Assert.False(first.SetupDone);
        var store = new SettingsStore(PathOf);
        store.Save(first);
        Assert.False(store.Load().SetupDone);
    }

    [Fact]
    public void TrayHintShown_DefaultsToTrue() => Assert.True(new AppSettings().TrayHintShown);

    [Fact]
    public void TrayHintShown_MissingInFile_LoadsTrue()
    {
        File.WriteAllText(PathOf, """{"settingsVersion":3}""");
        Assert.True(new SettingsStore(PathOf).Load().TrayHintShown);
    }

    [Fact]
    public void CreateForFirstRun_HintPending_AndRoundTrips()
    {
        var first = AppSettings.CreateForFirstRun();
        Assert.False(first.TrayHintShown);
        var store = new SettingsStore(PathOf);
        store.Save(first);
        Assert.False(store.Load().TrayHintShown);
        first.TrayHintShown = true;
        store.Save(first);
        Assert.True(store.Load().TrayHintShown);
    }

    [Fact]
    public void CreateForFirstRun_OtherwiseMatchesDefaults()
    {
        var first = AppSettings.CreateForFirstRun();
        var plain = new AppSettings();
        Assert.Equal(plain.ActiveProfile, first.ActiveProfile);
        Assert.Equal(GameProfile.DefaultName, first.ActiveProfile);
        Assert.Equal(plain.Profiles.Select(p => p.Name), first.Profiles.Select(p => p.Name));
        Assert.Equal(plain.TargetLang, first.TargetLang);
        Assert.Equal(plain.WebPort, first.WebPort);
        Assert.Equal(plain.Theme, first.Theme);
        Assert.Equal(plain.PictureSource, first.PictureSource);
        Assert.Equal(plain.Hotkeys, first.Hotkeys);
    }
}
