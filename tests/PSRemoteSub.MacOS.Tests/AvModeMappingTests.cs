using PSRemoteSub.Core.Capture;
using PSRemoteSub.MacOS.Capture;

namespace PSRemoteSub.MacOS.Tests;

public class AvModeMappingTests
{
    private static uint Fourcc(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);

    [Theory]
    [InlineData("420v")]
    [InlineData("420f")]
    public void ToMode_420v_IsNv12(string fourcc) =>
        Assert.Equal(CaptureSubtype.Nv12, AvModeMapping.ToMode(1920, 1080, 60, Fourcc(fourcc)).Subtype);

    [Theory]
    [InlineData("yuvs")]
    [InlineData("2vuy")]
    public void ToMode_Yuv422_IsYuy2(string fourcc) =>
        Assert.Equal(CaptureSubtype.Yuy2, AvModeMapping.ToMode(1920, 1080, 60, Fourcc(fourcc)).Subtype);

    [Fact]
    public void ToMode_dmb1_IsMjpg() =>
        Assert.Equal(CaptureSubtype.Mjpg, AvModeMapping.ToMode(1920, 1080, 60, Fourcc("dmb1")).Subtype);

    [Fact]
    public void ToMode_Unknown_IsOther() =>
        Assert.Equal(CaptureSubtype.Other, AvModeMapping.ToMode(1920, 1080, 60, Fourcc("BGRA")).Subtype);

    [Fact]
    public void ToMode_KeepsSizeAndFps()
    {
        var mode = AvModeMapping.ToMode(1280, 720, 29.97, Fourcc("420v"));
        Assert.Equal(new CaptureMode(1280, 720, 29.97, CaptureSubtype.Nv12), mode);
    }
}
