using PSRemoteSub.Core.Imaging;
using PSRemoteSub.MacOS.Capture;

namespace PSRemoteSub.MacOS.Tests;

public class RemotePlayWindowRulesTests
{
    private const string Bundle = "com.playstation.RemotePlay";

    private static MacWindow Win(uint id, string owner, string? bundle, double w, double h, bool onScreen = true, double x = 100, double y = 50) =>
        new(id, 500 + (int)id, owner, bundle, x, y, w, h, 0, onScreen);

    private static readonly MacScreen[] OneRetina = [new(0, 0, 1728, 1117, 2)];

    [Fact]
    public void Choose_PicksRemotePlayLargestWindow()
    {
        var chosen = RemotePlayWindowRules.Choose(
        [
            Win(1, "RemotePlay", Bundle, 300, 200),
            Win(2, "RemotePlay", Bundle, 1280, 752),
            Win(3, "RemotePlay", Bundle, 640, 400),
        ]);
        Assert.Equal(2u, chosen?.Id);
    }

    [Fact]
    public void Choose_IgnoresOtherApps()
    {
        var chosen = RemotePlayWindowRules.Choose(
        [
            Win(1, "Safari", "com.apple.Safari", 1700, 1000),
            Win(2, "RemotePlay", Bundle, 800, 480),
            Win(3, "Finder", "com.apple.finder", 1200, 900),
        ]);
        Assert.Equal(2u, chosen?.Id);
    }

    [Theory]
    [InlineData("RemotePlay")]
    [InlineData("PS Remote Play")]
    public void Choose_OwnerName_WhenBundleIdUnknown(string owner)
    {
        var chosen = RemotePlayWindowRules.Choose([Win(1, "Notes", null, 900, 900), Win(2, owner, null, 800, 480)]);
        Assert.Equal(2u, chosen?.Id);
    }

    [Fact]
    public void Choose_BundleIdBeatsOwnerName()
    {
        var chosen = RemotePlayWindowRules.Choose([Win(1, "RemotePlay", "com.example.Fake", 1600, 900), Win(2, "RemotePlay", Bundle, 800, 480)]);
        Assert.Equal(2u, chosen?.Id);
    }

    [Fact]
    public void Choose_OffScreen_IsMinimized()
    {
        var chosen = RemotePlayWindowRules.Choose([Win(7, "RemotePlay", Bundle, 1280, 752, onScreen: false)]);
        Assert.NotNull(chosen);
        var info = RemotePlayWindowRules.ToWindowInfo(chosen, OneRetina, titleBarPoints: 32);
        Assert.True(info.IsMinimized);
        Assert.Equal((nint)7, info.Handle);
        Assert.Equal(507, info.ProcessId);
    }

    [Fact]
    public void Choose_PrefersOnScreenOverLargerHiddenWindow()
    {
        var chosen = RemotePlayWindowRules.Choose(
        [
            Win(1, "RemotePlay", Bundle, 1600, 1000, onScreen: false), // ordered-out window (.optionAll lists it)
            Win(2, "RemotePlay", Bundle, 1280, 752),
            Win(3, "RemotePlay", Bundle, 300, 200),
        ]);
        Assert.Equal(2u, chosen?.Id);
    }

    [Fact]
    public void Choose_NoneOnScreen_LargestOffScreen()
    {
        var chosen = RemotePlayWindowRules.Choose(
        [
            Win(1, "RemotePlay", Bundle, 300, 200, onScreen: false),
            Win(2, "RemotePlay", Bundle, 1280, 752, onScreen: false),
        ]);
        Assert.Equal(2u, chosen?.Id);
    }

    [Fact]
    public void Choose_None_ReturnsNull()
    {
        Assert.Null(RemotePlayWindowRules.Choose([]));
        Assert.Null(RemotePlayWindowRules.Choose([Win(1, "Safari", "com.apple.Safari", 1700, 1000)]));
        Assert.Null(RemotePlayWindowRules.Choose([Win(1, "RemotePlay", Bundle, 0, 0)]));
    }

    [Fact]
    public void ToWindowInfo_Windowed_DropsTitleBar()
    {
        var info = RemotePlayWindowRules.ToWindowInfo(Win(1, "RemotePlay", Bundle, 1280, 752, x: 100, y: 50), OneRetina, titleBarPoints: 32);
        Assert.Equal(new PixelRect(100, 82, 1280, 720), info.ClientScreenRect);
        Assert.False(info.IsMinimized);
    }

    [Fact]
    public void ToWindowInfo_FullScreen_KeepsWholeWindow()
    {
        var info = RemotePlayWindowRules.ToWindowInfo(Win(1, "RemotePlay", Bundle, 1728, 1117, x: 0, y: 0), OneRetina, titleBarPoints: 32);
        Assert.Equal(new PixelRect(0, 0, 1728, 1117), info.ClientScreenRect);
    }

    [Fact]
    public void ToWindowInfo_FullScreenOnSecondScreen_KeepsWholeWindow()
    {
        MacScreen[] screens = [new(0, 0, 1728, 1117, 2), new(-1920, 0, 1920, 1080, 1)];
        var info = RemotePlayWindowRules.ToWindowInfo(Win(1, "RemotePlay", Bundle, 1920, 1080, x: -1920, y: 0), screens, titleBarPoints: 32);
        Assert.Equal(new PixelRect(-1920, 0, 1920, 1080), info.ClientScreenRect);
    }
}
