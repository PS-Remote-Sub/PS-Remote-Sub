using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Tests.Capture;

public class CaptureModeChooserTests
{
    private static List<CaptureMode> TestCard()
    {
        var list = new List<CaptureMode>();
        foreach (var subtype in new[] { CaptureSubtype.Yuy2, CaptureSubtype.Nv12, CaptureSubtype.Mjpg })
        {
            foreach (var fps in new double[] { 50, 30, 25, 20, 10 })
                list.Add(new CaptureMode(2560, 1440, fps, subtype));
            foreach (var fps in new double[] { 60, 50, 30, 20, 10 })
                list.Add(new CaptureMode(1920, 1080, fps, subtype));
            foreach (var fps in new double[] { 60, 10 })
                list.Add(new CaptureMode(1280, 720, fps, subtype));
        }
        return list;
    }

    [Fact]
    public void Choose_Auto_TestCard_Picks1080p60Nv12() =>
        Assert.Equal(new CaptureMode(1920, 1080, 60, CaptureSubtype.Nv12), CaptureModeChooser.Choose(TestCard(), null));

    [Fact]
    public void Choose_Wanted1440p50_PicksIt() =>
        Assert.Equal(new CaptureMode(2560, 1440, 50, CaptureSubtype.Nv12),
            CaptureModeChooser.Choose(TestCard(), new CaptureModeSetting(2560, 1440, 50)));

    [Fact]
    public void Choose_WantedModeMissing_FallsBackToAuto() =>
        Assert.Equal(new CaptureMode(1920, 1080, 60, CaptureSubtype.Nv12),
            CaptureModeChooser.Choose(TestCard(), new CaptureModeSetting(3840, 2160, 60)));

    [Fact]
    public void Choose_Auto_No60fps_PicksHighestRateThenLargest()
    {
        var offered = new[]
        {
            new CaptureMode(1920, 1080, 30, CaptureSubtype.Yuy2),
            new CaptureMode(1280, 720, 30, CaptureSubtype.Yuy2),
            new CaptureMode(2560, 1440, 25, CaptureSubtype.Yuy2),
        };
        Assert.Equal(offered[0], CaptureModeChooser.Choose(offered, null));
    }

    [Fact]
    public void Choose_Auto_59_94CountsAs60()
    {
        var offered = new[]
        {
            new CaptureMode(1920, 1080, 59.94, CaptureSubtype.Nv12),
            new CaptureMode(2560, 1440, 50, CaptureSubtype.Nv12),
        };
        Assert.Equal(offered[0], CaptureModeChooser.Choose(offered, null));
    }

    [Fact]
    public void Choose_IgnoresAbove4kAnd60()
    {
        var offered = new[]
        {
            new CaptureMode(3840, 2160, 60, CaptureSubtype.Nv12),
            new CaptureMode(4096, 2160, 60, CaptureSubtype.Nv12),
            new CaptureMode(1920, 1080, 120, CaptureSubtype.Nv12),
        };
        Assert.Equal(offered[0], CaptureModeChooser.Choose(offered, null));
    }

    [Fact]
    public void Choose_EmptyOrOnlyOther_ReturnsNull()
    {
        Assert.Null(CaptureModeChooser.Choose(Array.Empty<CaptureMode>(), null));
        Assert.Null(CaptureModeChooser.Choose(new[] { new CaptureMode(1920, 1080, 60, CaptureSubtype.Other) }, null));
    }

    [Fact]
    public void Fallback_Uncompressed_GivesMjpgSameMode() =>
        Assert.Equal(new CaptureMode(2560, 1440, 50, CaptureSubtype.Mjpg),
            CaptureModeChooser.Fallback(TestCard(), new CaptureMode(2560, 1440, 50, CaptureSubtype.Nv12)));

    [Fact]
    public void Fallback_FromMjpg_ReturnsNull() =>
        Assert.Null(CaptureModeChooser.Fallback(TestCard(), new CaptureMode(2560, 1440, 50, CaptureSubtype.Mjpg)));

    [Fact]
    public void Choices_TestCard_DistinctOrdered()
    {
        var choices = CaptureModeChooser.Choices(TestCard());
        Assert.Equal(12, choices.Count);
        Assert.Equal(new CaptureModeSetting(2560, 1440, 50), choices[0]);
        Assert.Equal(new CaptureModeSetting(2560, 1440, 30), choices[1]);
        Assert.Equal(new CaptureModeSetting(2560, 1440, 25), choices[2]);
    }
}
