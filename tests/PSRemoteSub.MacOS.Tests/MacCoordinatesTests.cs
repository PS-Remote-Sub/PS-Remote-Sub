using PSRemoteSub.Core.Imaging;
using PSRemoteSub.MacOS.Capture;

namespace PSRemoteSub.MacOS.Tests;

/// <summary>
/// Avalonia 12 on macOS uses Quartz global points (top-left of the main display, Y down, Scaling 1) for
/// <c>Window.Position</c> and <c>Screen.Bounds</c>, so the overlay rect is the window rect in points on every screen.
/// </summary>
public class MacCoordinatesTests
{
    [Fact]
    public void ToScreenPixels_Retina_KeepsPoints() =>
        Assert.Equal(new PixelRect(100, 82, 1280, 720), MacCoordinates.ToScreenPixels(100, 82, 1280, 720));

    [Fact]
    public void ToScreenPixels_SecondScreenScale1_LeftOfMain() =>
        Assert.Equal(new PixelRect(-1820, 40, 800, 450), MacCoordinates.ToScreenPixels(-1820, 40, 800, 450));

    [Fact]
    public void ToScreenPixels_ScreenAboveMain() =>
        Assert.Equal(new PixelRect(200, -1000, 960, 540), MacCoordinates.ToScreenPixels(200, -1000, 960, 540));

    [Fact]
    public void ToScreenPixels_FractionalPoints_RoundsOutward() =>
        Assert.Equal(new PixelRect(10, -6, 102, 51), MacCoordinates.ToScreenPixels(10.5, -5.5, 100.8, 50.2));

    [Fact]
    public void TopInsetRows_RetinaWindowed_IsTitleBarInPixels() =>
        // Window 1280x752 pt at 2x = 2560x1504 px; client 1280x720 pt -> 64 px of title bar.
        Assert.Equal(64, MacCoordinates.TopInsetRows(2560, 1504, new PixelRect(100, 82, 1280, 720)));

    [Fact]
    public void TopInsetRows_FullScreen_IsZero() =>
        Assert.Equal(0, MacCoordinates.TopInsetRows(3456, 2234, new PixelRect(0, 0, 1728, 1117)));

    [Fact]
    public void TopInsetRows_StaleOrEmptyClient_NeverNegativeOrWholeFrame()
    {
        Assert.Equal(0, MacCoordinates.TopInsetRows(2560, 1440, new PixelRect(0, 0, 1280, 752)));
        Assert.Equal(0, MacCoordinates.TopInsetRows(2560, 1504, new PixelRect(0, 0, 0, 0)));
        Assert.Equal(0, MacCoordinates.TopInsetRows(0, 0, new PixelRect(0, 0, 1280, 720)));
        Assert.InRange(MacCoordinates.TopInsetRows(2560, 1504, new PixelRect(0, 0, 1280, 10)), 0, 1504 / 4);
    }
}
