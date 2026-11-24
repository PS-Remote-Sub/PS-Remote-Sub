using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Layout;
using PSRemoteSub.Core.Text;

namespace PSRemoteSub.Core.Tests.Layout;

public class SubtitleBoundsTests
{
    // Frame 40x20, region = the bottom half (0,10,40,10), OCR image upscaled 2x.
    private static readonly PixelRect Region = new(0, 10, 40, 10);

    private static RegionFraction? Of(double scale, params PixelRect[] lines) =>
        SubtitleBounds.FromOcr([.. lines.Select(b => new OcrLine("x", b))], scale, Region, 40, 20);

    [Fact]
    public void NoLines_Null() => Assert.Null(Of(2));

    [Fact]
    public void OneLine_Scale2_MappedBackInflatedAndOffset() =>
        // Capture (10,12,20,6); margin max(4, round(6/4)) = 4 → (6,8,28,14); bottom clipped at 20 → (6,8,28,12).
        Assert.Equal(new RegionFraction(0.15, 0.4, 0.7, 0.6), Of(2, new PixelRect(20, 4, 40, 12)));

    [Fact]
    public void TwoLines_UnionCoversBoth()
    {
        // Scale 1: capture (5,11,10,2) and (20,15,15,2) → union (5,11,30,6) → margin 4 → (1,7,38,13) → clipped (1,7,38,13).
        var b = Of(1, new PixelRect(5, 1, 10, 2), new PixelRect(20, 5, 15, 2))!;
        var px = b.ToPixels(40, 20);
        Assert.Equal(new PixelRect(1, 7, 38, 13), px);
    }

    [Fact]
    public void NoiseLine_DoesNotWidenBounds()
    {
        // A HUD edge read as "|" at the region's left must not stretch the box away from the subtitle.
        var lines = new[] { new OcrLine("|", new PixelRect(0, 0, 2, 10)), new OcrLine("Hello", new PixelRect(20, 4, 40, 12)) };
        Assert.Equal(new RegionFraction(0.15, 0.4, 0.7, 0.6), SubtitleBounds.FromOcr(lines, 2, Region, 40, 20));
    }

    [Fact]
    public void EmptyBoundsLine_Ignored()
    {
        var lines = new[] { new OcrLine("Hi", default), new OcrLine("Hello", new PixelRect(20, 4, 40, 12)) };
        Assert.Equal(new RegionFraction(0.15, 0.4, 0.7, 0.6), SubtitleBounds.FromOcr(lines, 2, Region, 40, 20));
    }

    [Fact]
    public void ForRows_OneRectPerRead_SameMarginAsTheUnion()
    {
        // Scale 1, margin 4 (from the lines' heights, as for the union): read 1 (5,11,10,2) → (1,7,18,10);
        // read 2 (20,15,15,2) → (16,11,23,10), clipped at the frame bottom → (16,11,23,9).
        var lines = new[] { new OcrLine("Cop: Hi", new PixelRect(5, 1, 10, 2)), new OcrLine("Jay: Yo", new PixelRect(20, 5, 15, 2)) };
        var reads = new[]
        {
            new SpeakerRead("Cop", "Hi", null, new PixelRect(5, 1, 10, 2)),
            new SpeakerRead("Jay", "Yo", null, new PixelRect(20, 5, 15, 2)),
        };
        var rows = SubtitleBounds.ForRows(reads, lines, 1, Region, 40, 20)!;
        Assert.Equal([new PixelRect(1, 7, 18, 10), new PixelRect(16, 11, 23, 9)], rows.Select(r => r.ToPixels(40, 20)));
    }

    [Fact]
    public void ForRows_ReadWithoutBox_Null()
    {
        var lines = new[] { new OcrLine("Hi", new PixelRect(5, 1, 10, 2)) };
        Assert.Null(SubtitleBounds.ForRows([new SpeakerRead(null, "Hi", null)], lines, 1, Region, 40, 20));
        Assert.Null(SubtitleBounds.ForRows([], lines, 1, Region, 40, 20));
    }

    [Fact]
    public void OnlyNoise_Null() =>
        Assert.Null(SubtitleBounds.FromOcr([new OcrLine("|", new PixelRect(0, 0, 2, 10))], 1, Region, 40, 20));

    [Fact]
    public void AtFrameEdge_ClippedInside()
    {
        var b = Of(1, new PixelRect(0, 0, 40, 10))!;
        Assert.True(b.X >= 0 && b.Y >= 0);
        Assert.True(b.X + b.Width <= 1 + 1e-9);
        Assert.True(b.Y + b.Height <= 1 + 1e-9);
        Assert.Equal(new PixelRect(0, 6, 40, 14), b.ToPixels(40, 20));
    }
}
