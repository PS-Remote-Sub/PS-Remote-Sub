using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Layout;

namespace PSRemoteSub.Core.Tests.Layout;

public sealed class FitRowsTests
{
    private static readonly PixelRect Client = new(0, 0, 1920, 1080);
    private const int Gap = 8;

    private static IReadOnlyList<PixelRect> Fit(PixelRect[] english, (int W, int H)[] thai) =>
        OverlayLayout.ComputeFitRows(Client, english, [.. thai.Select(t => (t.W, t.H))], Gap);

    [Fact]
    public void SmallerThai_CoversEachEnglishLineExactly()
    {
        PixelRect[] english = [new(100, 100, 400, 40), new(80, 150, 440, 40)];
        Assert.Equal(english, Fit(english, [(300, 30), (350, 30)]));
    }

    [Fact]
    public void WiderThai_GrowsAboutTheEnglishCentre()
    {
        Assert.Equal([new PixelRect(50, 100, 500, 40)], Fit([new(100, 100, 400, 40)], [(500, 30)]));
    }

    [Fact]
    public void TallerThai_GrowsAboutTheEnglishMiddle()
    {
        Assert.Equal([new PixelRect(100, 90, 400, 60)], Fit([new(100, 100, 400, 40)], [(300, 60)]));
    }

    // The English boxes come with a margin, so neighbours can touch: each gives up half the gap from its margin.
    [Fact]
    public void TouchingLines_KeepTheGap_FromTheirMargins()
    {
        Assert.Equal([new PixelRect(100, 100, 400, 46), new PixelRect(100, 154, 400, 46)],
            Fit([new(100, 100, 400, 50), new(100, 150, 400, 50)], [(300, 30), (300, 30)]));
    }

    // Row 1's Thai fills it (no room to give), so row 2 gives up its spare 10 px and then moves down the rest.
    [Fact]
    public void TallThaiAbove_PushesTheNextRowDown()
    {
        Assert.Equal([new PixelRect(100, 80, 400, 80), new PixelRect(100, 168, 400, 30)],
            Fit([new(100, 100, 400, 40), new(100, 150, 400, 40)], [(300, 80), (300, 30)]));
    }

    [Fact]
    public void KeptInsideTheClient()
    {
        Assert.Equal([new PixelRect(0, 100, 600, 40)], Fit([new(10, 100, 100, 40)], [(600, 30)]));
    }
}
