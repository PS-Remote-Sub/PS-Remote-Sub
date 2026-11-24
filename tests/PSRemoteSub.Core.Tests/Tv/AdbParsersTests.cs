using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Core.Tests.Tv;

public sealed class AdbParsersTests
{
    [Fact]
    public void MdnsServices_ParsesRealOutput_AndKeepsOnlyAdbTcp()
    {
        const string stdout = "List of discovered mdns services\r\n" +
                              "adb-1a00bf004003264\t_adb._tcp\t192.168.0.33:5555\r\n" +
                              "adb-1a00bf004003264\t_adb-tls-connect._tcp\t192.168.0.33:37000\r\n" +
                              "adb-1a00bf004003264\t_adb-tls-pairing._tcp\t192.168.0.33:37001\r\n";
        var services = AdbParsers.MdnsServices(stdout);
        var one = Assert.Single(services);
        Assert.Equal("adb-1a00bf004003264", one.Name);
        Assert.Equal("192.168.0.33:5555", one.Endpoint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("List of discovered mdns services\r\n")]
    public void MdnsServices_NoServices_Empty(string stdout) => Assert.Empty(AdbParsers.MdnsServices(stdout));

    [Theory]
    [InlineData("failed to authenticate to 192.168.0.33:5555", AdbConnect.Unauthorized)]
    [InlineData("already connected to 192.168.0.33:5555", AdbConnect.Connected)]
    [InlineData("connected to 192.168.0.33:5555\r\n", AdbConnect.Connected)]
    [InlineData("failed to connect to '192.168.0.33:5555': Connection refused", AdbConnect.Failed)]
    [InlineData("cannot connect to 192.168.0.33:5555: No connection could be made", AdbConnect.Failed)]
    [InlineData("", AdbConnect.Failed)]
    public void Connect_Classifies(string stdout, AdbConnect expected) => Assert.Equal(expected, AdbParsers.Connect(stdout));

    [Theory]
    [InlineData("device\r\n", "device")]
    [InlineData("unauthorized", "unauthorized")]
    [InlineData("  \r\n", null)]
    public void DeviceState_Trims(string stdout, string? expected) => Assert.Equal(expected, AdbParsers.DeviceState(stdout));

    [Fact]
    public void VersionCode_ParsesDumpsysLine()
    {
        const string dump = "Packages:\r\n  Package [th.psrs.tvoverlay] (abc):\r\n    versionCode=2 minSdk=26 targetSdk=36\r\n    versionName=1.0\r\n";
        Assert.Equal(2, AdbParsers.VersionCode(dump));
    }

    [Fact]
    public void VersionCode_NotInstalled_Null() => Assert.Null(AdbParsers.VersionCode("Unable to find package: th.psrs.tvoverlay\r\n"));

    [Theory]
    [InlineData("Performing Streamed Install\r\nSuccess\r\n", AdbInstallOutcome.Success)]
    [InlineData("Performing Streamed Install\r\nadb: failed to install a.apk: Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE: Existing package signatures do not match]", AdbInstallOutcome.SignatureMismatch)]
    [InlineData("Failure [INSTALL_FAILED_INSUFFICIENT_STORAGE]", AdbInstallOutcome.Failed)]
    [InlineData("", AdbInstallOutcome.Failed)]
    public void Install_Classifies(string output, AdbInstallOutcome expected) => Assert.Equal(expected, AdbParsers.Install(output));
}
