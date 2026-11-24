using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Tests.Capture;

public class LetterboxTests
{
    [Fact]
    public void Letterbox_SameAspect_FillsHost() =>
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), Letterbox.Fit(new PixelRect(0, 0, 1920, 1080), 1920, 1080));

    [Fact]
    public void Letterbox_TallHost_BarsTopBottom() =>
        Assert.Equal(new PixelRect(100, 268, 1000, 563), Letterbox.Fit(new PixelRect(100, 50, 1000, 1000), 1920, 1080));

    [Fact]
    public void Letterbox_WideHost_BarsLeftRight() =>
        Assert.Equal(new PixelRect(111, 0, 1778, 1000), Letterbox.Fit(new PixelRect(0, 0, 2000, 1000), 1920, 1080));

    [Fact]
    public void Letterbox_NoFrameSize_ReturnsHost()
    {
        var host = new PixelRect(5, 6, 700, 400);
        Assert.Equal(host, Letterbox.Fit(host, 0, 1080));
        Assert.Equal(host, Letterbox.Fit(host, 1920, 0));
        Assert.Equal(new PixelRect(0, 0, 0, 0), Letterbox.Fit(new PixelRect(0, 0, 0, 0), 1920, 1080));
    }
}
