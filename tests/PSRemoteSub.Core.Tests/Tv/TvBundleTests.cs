using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Core.Tests.Tv;

public sealed class TvBundleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tvbundle").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private void WriteVersion(string text)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "tv"));
        File.WriteAllText(Path.Combine(_dir, "tv", "apk-version.txt"), text);
    }

    [Fact]
    public void AdbPath_Windows_WinFolder() =>
        Assert.Equal(Path.Combine(_dir, "tv", "adb", "win", "adb.exe"), TvBundle.AdbPath(_dir, PlatformKind.Windows));

    [Fact]
    public void AdbPath_Mac_MacFolder() =>
        Assert.Equal(Path.Combine(_dir, "tv", "adb", "mac", "adb"), TvBundle.AdbPath(_dir, PlatformKind.MacOS));

    [Fact]
    public void ApkPath_IsUnderTvFolder() =>
        Assert.Equal(Path.Combine(_dir, "tv", "psrs-tv-overlay.apk"), TvBundle.ApkPath(_dir));

    [Theory]
    [InlineData("2", 2)]
    [InlineData("13\r\n", 13)]
    [InlineData("  7 \n", 7)]
    public void BundledVersion_ParsesTrimmedInteger(string text, int expected)
    {
        WriteVersion(text);
        Assert.Equal(expected, TvBundle.BundledVersion(_dir));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("2.5")]
    public void BundledVersion_Invalid_Null(string text)
    {
        WriteVersion(text);
        Assert.Null(TvBundle.BundledVersion(_dir));
    }

    [Fact]
    public void BundledVersion_MissingFile_Null() => Assert.Null(TvBundle.BundledVersion(_dir));
}
