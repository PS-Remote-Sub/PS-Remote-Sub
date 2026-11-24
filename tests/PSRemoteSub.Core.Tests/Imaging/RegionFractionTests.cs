using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Tests.Imaging;

public class RegionFractionTests
{
    [Fact] public void ToPixels_ScalesAndRounds() =>
        Assert.Equal(new PixelRect(192, 864, 1536, 162),
            new RegionFraction(0.1, 0.8, 0.8, 0.15).ToPixels(1920, 1080));

    [Fact] public void ToPixels_ClipsToBounds() =>
        Assert.Equal(new PixelRect(1800, 1026, 120, 54),
            new RegionFraction(0.9375, 0.95, 0.5, 0.5).ToPixels(1920, 1080));

    [Fact] public void FromPixels_RoundTrips()
    {
        var r = RegionFraction.FromPixels(new PixelRect(100, 600, 800, 90), 1280, 720);
        Assert.Equal(new PixelRect(100, 600, 800, 90), r.ToPixels(1280, 720));
    }
}
