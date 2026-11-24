namespace PSRemoteSub.Core.Imaging;

public sealed record RegionFraction(double X, double Y, double Width, double Height)
{
    /// <summary>Rounds to pixels, clips to the bounds, and guarantees at least 1x1.</summary>
    public PixelRect ToPixels(int width, int height)
    {
        int x = Math.Clamp(Round(X * width), 0, Math.Max(width - 1, 0));
        int y = Math.Clamp(Round(Y * height), 0, Math.Max(height - 1, 0));
        int w = Math.Clamp(Round(Width * width), 1, Math.Max(width - x, 1));
        int h = Math.Clamp(Round(Height * height), 1, Math.Max(height - y, 1));
        return new PixelRect(x, y, w, h);
    }

    public static RegionFraction FromPixels(PixelRect r, int width, int height) =>
        new((double)r.X / width, (double)r.Y / height, (double)r.Width / width, (double)r.Height / height);

    private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
}
