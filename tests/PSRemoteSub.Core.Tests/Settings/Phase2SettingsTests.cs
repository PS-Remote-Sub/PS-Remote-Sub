using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Tests.Settings;

public sealed class Phase2SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "psrs-" + Guid.NewGuid().ToString("N"));
    private string PathOf => Path.Combine(_dir, "settings.json");

    public Phase2SettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void NewSettings_HaveGtaViProfile_Active()
    {
        var s = new SettingsStore(PathOf).Load();
        var p = Assert.Single(s.Profiles);
        Assert.Equal("Grand Theft Auto VI", p.Name);
        Assert.Equal("Grand Theft Auto VI", p.GameTitle);
        Assert.Equal("gta6", p.LibraryGame);
        Assert.Single(p.Zones);
        Assert.Equal("Grand Theft Auto VI", s.ActiveProfile);
        Assert.Same(p, s.GetActive());
    }

    [Fact]
    public void V2File_LegacyDefaultName_GetsGta6_KeepsName()
    {
        File.WriteAllText(PathOf, "{\"settingsVersion\":2,\"profiles\":[{\"name\":\"ค่าเริ่มต้น\"}],\"activeProfile\":\"ค่าเริ่มต้น\"}");
        var s = new SettingsStore(PathOf).Load();
        Assert.Equal("ค่าเริ่มต้น", s.Profiles[0].Name);
        Assert.Equal("gta6", s.Profiles[0].LibraryGame);
    }

    [Fact]
    public void V2File_NewDefaultName_GetsGta6()
    {
        File.WriteAllText(PathOf, "{\"settingsVersion\":2,\"profiles\":[{\"name\":\"Grand Theft Auto VI\"}]}");
        Assert.Equal("gta6", new SettingsStore(PathOf).Load().Profiles[0].LibraryGame);
    }

    [Fact]
    public void NewProfile_MinDisplayIsZero() => Assert.Equal(0, GameProfile.Create("x").MinDisplaySeconds);

    [Fact]
    public void FileWithMinDisplay3_Keeps3()
    {
        File.WriteAllText(PathOf, "{\"settingsVersion\":3,\"profiles\":[{\"name\":\"A\",\"minDisplaySeconds\":3}]}");
        Assert.Equal(3, new SettingsStore(PathOf).Load().Profiles[0].MinDisplaySeconds);
    }

    [Fact]
    public void MinDisplayChoices_For()
    {
        Assert.Equal([0, 0.5, 1, 1.5, 2, 3], MinDisplayChoices.For(3));
        Assert.Equal([0, 0.5, 1, 1.5, 2], MinDisplayChoices.For(1));
        Assert.Equal([0, 0.5, 1, 1.5, 2], MinDisplayChoices.Values);
    }

    [Fact]
    public void SpeakerColors_DefaultsTrue_RoundTripsFalse()
    {
        Assert.True(GameProfile.Create("x").SpeakerColors);
        var store = new SettingsStore(PathOf);
        var s = store.Load();
        s.Profiles[0].SpeakerColors = false;
        store.Save(s);
        Assert.False(store.Load().Profiles[0].SpeakerColors);
    }

    [Fact]
    public void CreateDefault_IsGtaVi()
    {
        var p = GameProfile.CreateDefault();
        Assert.Equal(GameProfile.DefaultName, p.Name);
        Assert.Equal("gta6", p.LibraryGame);
        Assert.Equal("ค่าเริ่มต้น", GameProfile.LegacyDefaultName);
        Assert.True(GameProfile.IsDefaultName("ค่าเริ่มต้น"));
        Assert.True(GameProfile.IsDefaultName("Grand Theft Auto VI"));
        Assert.False(GameProfile.IsDefaultName("Other"));
    }
}
