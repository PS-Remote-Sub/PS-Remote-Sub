using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Core.Tests.Tv;

public sealed class FrameMappingTests
{
    [Fact]
    public void ToFrame_16by9Client_IsIdentity()
    {
        var r = FrameMapping.ToFrame(new(0.1, 0.8, 0.8, 0.1), 1920, 1080);
        Assert.Equal(0.1, r.X, 6);
        Assert.Equal(0.8, r.Y, 6);
        Assert.Equal(0.8, r.Width, 6);
        Assert.Equal(0.1, r.Height, 6);
    }

    [Fact]
    public void ToFrame_4by3Client_RemovesTopAndBottomBars()
    {   // 1440x1080 client -> 1440x810 frame at y=135
        var r = FrameMapping.ToFrame(new(0, 135.0 / 1080, 1, 810.0 / 1080), 1440, 1080);
        Assert.Equal(0, r.Y, 6);
        Assert.Equal(1, r.Height, 6);
    }

    [Fact]
    public void ToFrame_21by9Client_RemovesSideBars()
    {   // 2520x1080 client -> 1920x1080 frame at x=300
        var r = FrameMapping.ToFrame(new(300.0 / 2520, 0, 1920.0 / 2520, 1), 2520, 1080);
        Assert.Equal(0, r.X, 6);
        Assert.Equal(1, r.Width, 6);
    }

    [Fact]
    public void ToFrame_InsideBar_IsClamped()
    {
        var r = FrameMapping.ToFrame(new(0, 0, 1, 0.05), 1440, 1080);
        Assert.True(r.Y >= 0);
        Assert.True(r.Height >= 0);
    }
}
