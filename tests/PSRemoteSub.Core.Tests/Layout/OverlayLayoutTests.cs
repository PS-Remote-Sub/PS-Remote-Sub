using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Layout;

namespace PSRemoteSub.Core.Tests.Layout;

public class OverlayLayoutTests
{
    private static readonly PixelRect Client = new(0, 0, 1920, 1080);
    private static readonly RegionFraction Region = new(0.1, 0.8, 0.8, 0.15);

    // Caption box (576,918,768,86) in the 1920x1080 client.
    private static readonly RegionFraction Caption = new(0.3, 0.85, 0.4, 0.08);

    [Fact] public void Caption_ContentFits_IsTheBox() =>
        Assert.Equal(new PixelRect(576, 918, 768, 86), OverlayLayout.ComputeCaption(Client, Caption, 60));

    [Fact] public void Caption_ContentTaller_GrowsUpOnly() =>
        Assert.Equal(new PixelRect(576, 864, 768, 140), OverlayLayout.ComputeCaption(Client, Caption, 140));

    [Fact]
    public void Caption_GrowthClampedAtClientTop()
    {
        var client = new PixelRect(100, 50, 1920, 1080);
        var r = OverlayLayout.ComputeCaption(client, new RegionFraction(0.3, 0.02, 0.4, 0.08), 400);
        Assert.Equal(50, r.Y);
        Assert.Equal(400, r.Height);
    }

    [Fact]
    public void Caption_NeverTallerThanClient()
    {
        var r = OverlayLayout.ComputeCaption(Client, Caption, 5000);
        Assert.Equal(1080, r.Height);
        Assert.Equal(0, r.Y);
    }

    [Fact]
    public void Caption_OffsetClient_AddsOrigin()
    {
        var r = OverlayLayout.ComputeCaption(new PixelRect(2560, 0, 2880, 1620), Caption, 50);
        Assert.Equal(2560 + 864, r.X);
        Assert.Equal(1377, r.Y);
    }

    [Fact] public void Caption_NonPositiveContent_KeepsTheBox() =>
        Assert.Equal(new PixelRect(576, 918, 768, 86), OverlayLayout.ComputeCaption(Client, Caption, 0));

    // Text bounds (768,918,384,54) in the 1920x1080 client; region (192,864,1536,162).
    private static readonly RegionFraction Text = new(0.4, 0.85, 0.2, 0.05);

    [Fact] public void Fit_ShortText_BoxEqualsTextBounds() =>
        Assert.Equal(new PixelRect(768, 918, 384, 54), OverlayLayout.ComputeFit(Client, Region, Text, 200, 40, 0));

    [Fact] public void Fit_LongText_GrowsBothWaysEqually() =>
        Assert.Equal(new PixelRect(660, 918, 600, 54), OverlayLayout.ComputeFit(Client, Region, Text, 600, 40, 0));

    [Fact] public void Fit_TextWiderThanRegion_CappedAtRegionWidth() =>
        Assert.Equal(new PixelRect(192, 905, 1536, 80), OverlayLayout.ComputeFit(Client, Region, Text, 3000, 80, 0));

    [Fact] public void Fit_PreviousLine_AddsAboveOnly() =>
        Assert.Equal(new PixelRect(768, 888, 384, 84), OverlayLayout.ComputeFit(Client, Region, Text, 200, 40, 30));

    [Fact]
    public void Fit_NearClientBottom_ShiftedInside()
    {
        var r = OverlayLayout.ComputeFit(Client, Region, new RegionFraction(0.4, 0.97, 0.2, 0.05), 200, 80, 0);
        Assert.Equal(Client.Bottom, r.Bottom);
        Assert.Equal(80, r.Height);
    }

    [Fact]
    public void Fit_PreviousLineNearClientTop_ShiftedInside()
    {
        var r = OverlayLayout.ComputeFit(Client, Region, new RegionFraction(0.4, 0.01, 0.2, 0.05), 200, 40, 60);
        Assert.Equal(Client.Y, r.Y);
        Assert.True(r.Height >= 100);
    }

    [Fact]
    public void Fit_OffsetClient_UsesClientOrigin()
    {
        var client = new PixelRect(2560, 100, 1920, 1080);
        Assert.Equal(new PixelRect(2560 + 768, 100 + 918, 384, 54), OverlayLayout.ComputeFit(client, Region, Text, 200, 40, 0));
    }
}
