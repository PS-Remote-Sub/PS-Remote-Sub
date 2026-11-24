using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Windows.Capture.Interop;

namespace PSRemoteSub.Windows.Capture;

/// <summary>Reports where the capture card picture is on screen, so the subtitle overlay can sit on it.</summary>
public static class CaptureSurfaceProbe
{
    /// <summary>
    /// The on-screen picture inside <paramref name="surface"/> in physical pixels (the app is PerMonitorV2 DPI aware),
    /// letterboxed to <paramref name="frameSize"/> (the whole surface without one), reported as a window of
    /// <paramref name="topLevel"/>. Null when the surface does not exist.
    /// </summary>
    public static WindowInfo? Measure(nint topLevel, nint surface, (int Width, int Height)? frameSize)
    {
        if (surface == 0 || !User32.IsWindow(surface))
            return null;
        if (!User32.GetClientRect(surface, out RECT client))
            return null;
        var origin = new POINT { X = client.Left, Y = client.Top };
        if (!User32.ClientToScreen(surface, ref origin))
            return null;

        var host = new PixelRect(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
        PixelRect picture = frameSize is var (width, height) ? Letterbox.Fit(host, width, height) : host;
        return new WindowInfo(topLevel, Environment.ProcessId, picture, User32.IsIconic(topLevel));
    }
}
