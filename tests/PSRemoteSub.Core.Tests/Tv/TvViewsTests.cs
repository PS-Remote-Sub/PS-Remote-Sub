using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Layout;
using PSRemoteSub.Core.Settings;
using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Core.Tests.Tv;

public sealed class TvViewsTests
{
    private static OverlayLine Line(string text, bool ai = false, RegionFraction? bounds = null,
        IReadOnlyList<SpeakerTag?>? speakers = null, IReadOnlyList<RegionFraction>? rows = null) =>
        new(1, text, ai, DateTimeOffset.UnixEpoch, bounds, speakers, rows);

    [Fact]
    public void Views_SplitsRowsWithSpeakersAndCovers()
    {
        var zone = new OcrZone { CaptionRegion = new(0.1, 0.7, 0.8, 0.2) };
        var r0 = new RegionFraction(0.1, 0.7, 0.8, 0.1);
        var r1 = new RegionFraction(0.1, 0.8, 0.8, 0.1);
        var view = TvViews.From(zone, Line("a\nb", true, null, [new SpeakerTag("Lucia", "#FFFFFF"), null], [r0, r1]), 0.04, 1920, 1080);

        Assert.NotNull(view);
        Assert.Equal(2, view.Rows.Count);
        Assert.Equal("Lucia", view.Rows[0].Speaker?.Name);
        Assert.Equal("a", view.Rows[0].Text);
        Assert.Equal(r1.Y, view.Rows[1].Cover!.Y, 6);
        Assert.Equal(r1.Height, view.Rows[1].Cover!.Height, 6);
        Assert.True(view.Provisional);
        Assert.Equal(0.04, view.FontSize);
        Assert.Equal(TvPlacement.Custom, view.Placement);
    }

    [Fact]
    public void Views_NoCurrentLine_ReturnsNull()
    {
        var zone = new OcrZone { CaptionRegion = new(0.1, 0.7, 0.8, 0.2) };
        Assert.Null(TvViews.From(zone, null, 0.04, 1920, 1080));
        Assert.Null(TvViews.From(zone, Line("a"), 0.04, 0, 1080));
    }

    [Fact]
    public void Views_NoCaptionOrOcrBox_ReturnsNull() =>
        Assert.Null(TvViews.From(new OcrZone(), Line("a"), 0.04, 1920, 1080));

    [Fact]
    public void Views_FitZone_MapsPlacementAndKeepsOcrBox()
    {
        var zone = new OcrZone { Placement = OverlayPlacement.Fit, Region = new(0.1, 0.8, 0.8, 0.1) };
        var view = TvViews.From(zone, Line("a", false, new RegionFraction(0.2, 0.8, 0.6, 0.1)), 0.04, 1920, 1080);

        Assert.NotNull(view);
        Assert.Equal(TvPlacement.Fit, view.Placement);
        Assert.Equal(0.1, view.CaptionBox.X, 6);
        Assert.Equal(zone.Region.Y, view.OcrBox!.Y, 6);
        Assert.Equal(0.2, view.Bounds!.X, 6);
        Assert.False(view.Provisional);
    }

    [Fact]
    public void Views_EqualInputs_GiveEqualViews()
    {
        var zone = new OcrZone { CaptionRegion = new(0.1, 0.7, 0.8, 0.2) };
        Assert.Equal(TvViews.From(zone, Line("a"), 0.04, 1920, 1080), TvViews.From(zone, Line("a"), 0.04, 1920, 1080));
    }
}
