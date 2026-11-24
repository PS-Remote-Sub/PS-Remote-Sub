using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Tests.Settings;

public class MacSettingsMigrationTests
{
    [Fact]
    public void Apply_Port5000_Becomes5055_ReturnsTrue()
    {
        var s = new AppSettings { WebPort = 5000 };
        Assert.True(MacSettingsMigration.Apply(s));
        Assert.Equal(5055, s.WebPort);
    }

    [Fact]
    public void Apply_Port8080_Unchanged_ReturnsFalse()
    {
        var s = new AppSettings { WebPort = 8080 };
        Assert.False(MacSettingsMigration.Apply(s));
        Assert.Equal(8080, s.WebPort);
    }

    [Fact]
    public void Apply_FreshDefaults_Becomes5055()
    {
        var s = new AppSettings();
        MacSettingsMigration.Apply(s);
        Assert.Equal(5055, s.WebPort);
    }
}
