namespace PSRemoteSub.Core.Imaging;

/// <summary>Pure image filters applied to the cropped subtitle region before OCR.</summary>
public static class Preprocessor
{
    public static Frame Apply(Frame crop, PreprocessFilter filter, int maxImageDimension)
    {
        ArgumentNullException.ThrowIfNull(crop);
        var src = crop.Bgra;
        var dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            int b = src[i], g = src[i + 1], r = src[i + 2];
            byte v;
            switch (filter)
            {
                case PreprocessFilter.WhiteText:
                    int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    v = (min >= 215 && max - min <= 45) ? (byte)0 : (byte)255;
                    break;
                case PreprocessFilter.DarkText:
                    v = Luma(r, g, b) <= 110 ? (byte)0 : (byte)255;
                    break;
                default:
                    v = (byte)Luma(r, g, b);
                    break;
            }
            dst[i] = dst[i + 1] = dst[i + 2] = v;
            dst[i + 3] = 255;
        }
        var filtered = new Frame(crop.Width, crop.Height, dst);

        bool upscale = crop.Height > 0 && crop.Height < 120
            && (long)Math.Max(crop.Width, crop.Height) * 2 <= maxImageDimension;
        return upscale ? Upscale2x(filtered) : filtered;
    }

    private static int Luma(int r, int g, int b) =>
        (int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b, MidpointRounding.AwayFromZero);

    private static Frame Upscale2x(Frame f)
    {
        int w = f.Width, h = f.Height, ow = w * 2, oh = h * 2;
        var dst = new byte[ow * oh * 4];
        var src = f.Bgra;
        for (int y = 0; y < oh; y++)
        {
            double sy = Math.Clamp((y + 0.5) / 2 - 0.5, 0, h - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, h - 1);
            double fy = sy - y0;
            for (int x = 0; x < ow; x++)
            {
                double sx = Math.Clamp((x + 0.5) / 2 - 0.5, 0, w - 1);
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, w - 1);
                double fx = sx - x0;
                int o = (y * ow + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    double top = src[(y0 * w + x0) * 4 + c] * (1 - fx) + src[(y0 * w + x1) * 4 + c] * fx;
                    double bot = src[(y1 * w + x0) * 4 + c] * (1 - fx) + src[(y1 * w + x1) * 4 + c] * fx;
                    dst[o + c] = (byte)Math.Round(top * (1 - fy) + bot * fy);
                }
                dst[o + 3] = 255;
            }
        }
        return new Frame(ow, oh, dst);
    }
}
