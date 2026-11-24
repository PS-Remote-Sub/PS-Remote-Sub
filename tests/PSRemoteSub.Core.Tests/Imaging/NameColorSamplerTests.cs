using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Tests.Imaging;

public class NameColorSamplerTests
{
    private static readonly PixelRect Box = new(5, 5, 20, 10);

    /// <summary>A 30x20 frame: <paramref name="background"/> everywhere, <paramref name="text"/> on a glyph-like stripe pattern inside the box.</summary>
    private static Frame Make((byte R, byte G, byte B) text, Func<int, int, (byte R, byte G, byte B)> background)
    {
        const int w = 30, h = 20;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            bool glyph = x >= Box.X + 3 && x < Box.Right - 3 && y >= Box.Y + 2 && y < Box.Bottom - 2 && x % 3 != 0;
            var (r, g, b) = glyph ? text : background(x, y);
            int i = (y * w + x) * 4;
            px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
        }
        return new Frame(w, h, px);
    }

    private static (byte, byte, byte) Dark(int x, int y) => (16, 16, 16);

    private static (double H, double S, double L) Hsl(string hex)
    {
        double r = Convert.ToInt32(hex[1..3], 16) / 255.0, g = Convert.ToInt32(hex[3..5], 16) / 255.0, b = Convert.ToInt32(hex[5..7], 16) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d == 0) return (0, 0, l);
        double s = d / (1 - Math.Abs(2 * l - 1));
        double hue = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((hue + 360) % 360, s, l);
    }

    [Fact]
    public void PinkOnDark() => Assert.Equal("#E070C0", NameColorSampler.Sample(Make((0xE0, 0x70, 0xC0), Dark), Box));

    [Fact]
    public void WhiteOnDark() => Assert.Equal("#FFFFFF", NameColorSampler.Sample(Make((250, 250, 250), Dark), Box));

    [Fact]
    public void GreyOnDark_White() => Assert.Equal("#FFFFFF", NameColorSampler.Sample(Make((0x90, 0x90, 0x90), Dark), Box));

    [Fact]
    public void DarkBlueOnBlack_Lightened()
    {
        var hsl = Hsl(NameColorSampler.Sample(Make((0x20, 0x30, 0xA0), (_, _) => (0, 0, 0)), Box)!);
        Assert.True(hsl.L >= 0.6 - 0.01, $"lightness {hsl.L}");
        Assert.InRange(hsl.H, 225, 240);
    }

    [Fact]
    public void BusyBackground_StillPink()
    {
        var hsl = Hsl(NameColorSampler.Sample(Make((0xE0, 0x70, 0xC0), (x, y) =>
        {
            byte v = (byte)((x * 37 + y * 53) % 80);
            return (v, (byte)(80 - v), (byte)((v * 3) % 80));
        }), Box)!);
        Assert.InRange(hsl.H, 302, 332);
    }

    [Fact]
    public void BoxOutsideFrame_Null() =>
        Assert.Null(NameColorSampler.Sample(Make((250, 250, 250), Dark), new PixelRect(100, 100, 10, 10)));

    [Fact]
    public void EmptyBox_Null() =>
        Assert.Null(NameColorSampler.Sample(Make((250, 250, 250), Dark), new PixelRect(5, 5, 0, 10)));

    [Fact]
    public void PinkName_WithSomeWhitePixels_StaysPink()
    {
        // A white colon or glow in the label box: white is farther from the dark background than pink, but it is not
        // the name's colour.
        var frame = Make((0xE0, 0x70, 0xC0), Dark);
        var px = frame.Bgra;
        for (int y = Box.Y + 2; y < Box.Bottom - 2; y++)
        for (int x = Box.Right - 9; x < Box.Right - 3; x++)
        {
            int i = (y * 30 + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = 250;
        }
        var hsl = Hsl(NameColorSampler.Sample(frame, Box)!);
        Assert.InRange(hsl.H, 302, 332);
    }

    [Fact]
    public void NearWhiteTint_CountsAsWhite() =>
        Assert.Equal("#FFFFFF", NameColorSampler.Sample(Make((0xF9, 0xFB, 0xFF), Dark), Box));
}
