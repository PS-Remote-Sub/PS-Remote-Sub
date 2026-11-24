namespace PSRemoteSub.Core.Imaging;

/// <summary>The colour a game draws a speaker's name in, read from the captured frame.</summary>
public static class NameColorSampler
{
    /// <summary>Share of the box's pixels taken as text candidates: those farthest from the background colour.</summary>
    private const double CandidateShare = 0.3;
    /// <summary>Below this chroma (max − min channel, 0..1) a colour is white or grey.</summary>
    private const double MinChroma = 0.15;
    /// <summary>Above this HSL lightness a colour is white with a tint.</summary>
    private const double MaxLightness = 0.9;
    /// <summary>Darker names are lightened to this HSL lightness so they read on the dark overlay box.</summary>
    private const double MinLightness = 0.6;

    /// <summary>
    /// <c>#RRGGBB</c> for the text in <paramref name="box"/> (frame pixels), or null when the box has no pixels in the
    /// frame. The background is the median colour of the box's edge; the text is the pixels farthest from it.
    /// </summary>
    public static string? Sample(Frame frame, PixelRect box)
    {
        ArgumentNullException.ThrowIfNull(frame);
        int x0 = Math.Max(box.X, 0), y0 = Math.Max(box.Y, 0);
        int x1 = Math.Min(box.Right, frame.Width), y1 = Math.Min(box.Bottom, frame.Height);
        if (x1 <= x0 || y1 <= y0)
            return null;

        var px = frame.Bgra;
        int width = frame.Width;
        (int R, int G, int B) At(int x, int y)
        {
            int i = (y * width + x) * 4;
            return (px[i + 2], px[i + 1], px[i]);
        }

        var edge = new List<(int R, int G, int B)>();
        for (int x = x0; x < x1; x++)
        {
            edge.Add(At(x, y0));
            edge.Add(At(x, y1 - 1));
        }
        for (int y = y0 + 1; y < y1 - 1; y++)
        {
            edge.Add(At(x0, y));
            edge.Add(At(x1 - 1, y));
        }
        int bgR = Median(edge.Select(c => c.R)), bgG = Median(edge.Select(c => c.G)), bgB = Median(edge.Select(c => c.B));

        var pixels = new List<(int R, int G, int B, int D)>((x1 - x0) * (y1 - y0));
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            var (r, g, b) = At(x, y);
            pixels.Add((r, g, b, (r - bgR) * (r - bgR) + (g - bgG) * (g - bgG) + (b - bgB) * (b - bgB)));
        }
        // Text candidates: the pixels farthest from the background. White (a colon, a glow, the next word's edge) is
        // farther from a dark box than any colour, so among the candidates the most colourful half decides.
        int take = Math.Max(1, (int)(pixels.Count * CandidateShare));
        var candidates = pixels.OrderByDescending(p => p.D).Take(take).ToList();
        var text = candidates.OrderByDescending(p => Math.Max(p.R, Math.Max(p.G, p.B)) - Math.Min(p.R, Math.Min(p.G, p.B)))
            .Take(Math.Max(1, candidates.Count / 2)).ToList();
        double mr = text.Average(p => p.R) / 255, mg = text.Average(p => p.G) / 255, mb = text.Average(p => p.B) / 255;

        if (IsWhitish(mr, mg, mb))
            return "#FFFFFF";
        var (h, s, l) = ToHsl(mr, mg, mb);
        var (rr, gg, bb) = l < MinLightness ? FromHsl(h, s, MinLightness) : (mr, mg, mb);
        return $"#{To255(rr):X2}{To255(gg):X2}{To255(bb):X2}";
    }

    /// <summary>
    /// True for <c>#RRGGBB</c> that cannot tell a name from white dialogue: white, grey, or white with a faint tint
    /// (low chroma, or very light). Chroma is used, not HSL saturation, which is near 1 for a faintly tinted white.
    /// </summary>
    public static bool IsWhitish(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != 7 || hex[0] != '#'
            || !int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int rgb))
            return true;
        return IsWhitish((rgb >> 16 & 0xFF) / 255.0, (rgb >> 8 & 0xFF) / 255.0, (rgb & 0xFF) / 255.0);
    }

    private static bool IsWhitish(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        return max - min < MinChroma || (max + min) / 2 > MaxLightness;
    }

    private static int Median(IEnumerable<int> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static int To255(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);

    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d == 0)
            return (0, 0, l);
        double s = d / (1 - Math.Abs(2 * l - 1));
        double h = max == r ? 60 * (((g - b) / d + 6) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h, s, l);
    }

    private static (double R, double G, double B) FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return (r + m, g + m, b + m);
    }
}
