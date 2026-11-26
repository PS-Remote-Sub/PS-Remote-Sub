using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Tests.Settings;

public sealed class AppThemeSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "psrs-" + Guid.NewGuid().ToString("N"));
    private string PathOf => Path.Combine(_dir, "settings.json");

    public AppThemeSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Theme_DefaultsToDark() => Assert.Equal(AppTheme.Dark, new AppSettings().Theme);

    [Fact]
    public void Theme_MissingInFile_LoadsDark()
    {
        File.WriteAllText(PathOf, """{"settingsVersion":3}""");
        Assert.Equal(AppTheme.Dark, new SettingsStore(PathOf).Load().Theme);
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.System)]
    public void Theme_RoundTrips(AppTheme theme)
    {
        var store = new SettingsStore(PathOf);
        store.Save(new AppSettings { Theme = theme });
        Assert.Equal(theme, store.Load().Theme);
    }

    [Fact]
    public void Theme_UnknownValue_LoadsDarkAndKeepsOtherSettings()
    {
        File.WriteAllText(PathOf, """{"settingsVersion":3,"webPort":5123,"theme":"Neon"}""");
        var s = new SettingsStore(PathOf).Load();
        Assert.Equal(AppTheme.Dark, s.Theme);
        Assert.Equal(5123, s.WebPort);
    }
}
