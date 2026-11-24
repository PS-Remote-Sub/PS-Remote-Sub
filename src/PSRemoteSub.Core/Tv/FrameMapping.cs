using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Tv;

/// <summary>
/// Maps rectangles from fractions of the Remote Play client area to fractions of the game frame: the 16:9 picture
/// inside the client area, without the letterbox bars. The TV shows only the game frame.
/// </summary>
public static class FrameMapping
{
    public const double GameAspect = 16.0 / 9;

    /// <summary>The same rectangle as a fraction of the game frame, clamped to [0,1]; a rectangle inside a bar collapses to the frame edge.</summary>
    public static RegionFraction ToFrame(RegionFraction clientFraction, int clientWidth, int clientHeight)
    {
        ArgumentNullException.ThrowIfNull(clientFraction);
        var frame = Letterbox.Fit(new PixelRect(0, 0, clientWidth, clientHeight), 16, 9);
        if (frame.Width <= 0 || frame.Height <= 0)
            return clientFraction;

        double left = Math.Clamp((clientFraction.X * clientWidth - frame.X) / frame.Width, 0, 1);
        double top = Math.Clamp((clientFraction.Y * clientHeight - frame.Y) / frame.Height, 0, 1);
        double right = Math.Clamp(((clientFraction.X + clientFraction.Width) * clientWidth - frame.X) / frame.Width, 0, 1);
        double bottom = Math.Clamp(((clientFraction.Y + clientFraction.Height) * clientHeight - frame.Y) / frame.Height, 0, 1);
        return new RegionFraction(left, top, Math.Max(right - left, 0), Math.Max(bottom - top, 0));
    }
}
