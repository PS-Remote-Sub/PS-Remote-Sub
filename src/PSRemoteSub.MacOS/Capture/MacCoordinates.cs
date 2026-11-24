using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.MacOS.Capture;

/// <summary>
/// Converts macOS window geometry to the "screen pixels" of <see cref="Core.Platform.WindowInfo"/>, which the overlay
/// windows hand straight to Avalonia (<c>Window.Position</c>, and <c>Screen.Bounds</c> / <c>Screen.Scaling</c> to size
/// themselves).
/// </summary>
/// <remarks>
/// <para>
/// Convention: on macOS, Avalonia's "pixel" space is <b>Quartz global points</b> — origin at the top-left of the
/// primary display, Y down, one unit per point on every display (Retina included), and every <c>Screen.Scaling</c> is
/// 1. That is exactly the space CGWindowListCopyWindowInfo reports window bounds in, so a window rect in points is
/// already the rect Avalonia wants: no scaling, no flip, negative X/Y for displays left of / above the main one.
/// </para>
/// <para>
/// Evidence (Avalonia 12.1.6, native/Avalonia.Native/src/OSX): <c>Screens.mm</c> GetScreen sets
/// <c>Bounds = [screen frame]</c> (points) with <c>Y = ConvertPointY(origin).Y - height</c> and <c>Scaling = 1</c>;
/// <c>WindowBaseImpl.mm</c> GetPosition/SetPosition use the window frame's top-left in points through the same
/// <c>ConvertPointY</c>; <c>main.mm</c> <c>ConvertPointY(p) = NSMaxY(NSScreen.screens[0].frame) - p.Y</c>, i.e. Cocoa's
/// bottom-left space flipped about the primary screen, which is the Quartz global space. On the C# side
/// (src/Avalonia.Native) <c>AvnScreen.Refresh</c> copies those values unchanged (<c>ToAvaloniaPixelRect</c>),
/// <c>WindowImplBase.Position</c> is <c>Native.Position.ToAvaloniaPixelPoint()</c> and <c>TopLevelImpl.DesktopScaling</c>
/// is 1. Measured on the development Mac (one built-in Retina display, 1728×1117 points, backing scale 2) from the dev
/// app bundle: <c>Screens.All</c> = bounds 0,0,1728,1117, working area 0,33,1728,1022 (menu bar at the top, so Y grows
/// down), Scaling 1, <c>DesktopScaling</c> 1, <c>RenderScaling</c> 2; CoreGraphics reports the same display as
/// CGDisplayBounds 0,0,1728,1117 (and NSScreen frame 0,0,1728,1117, backingScaleFactor 2).
/// </para>
/// <para>
/// Captured frames, by contrast, are in backing pixels (2× on Retina); the pipeline only relates the two through
/// fractions of the client area, so the different units never mix.
/// </para>
/// </remarks>
internal static class MacCoordinates
{
    /// <summary>
    /// A rect in Quartz global points as Avalonia screen "pixels" (see the remarks: the same units), rounded outward
    /// so the result covers the whole rect.
    /// </summary>
    public static PixelRect ToScreenPixels(double x, double y, double w, double h)
    {
        int left = (int)Math.Floor(x);
        int top = (int)Math.Floor(y);
        int right = (int)Math.Ceiling(x + w);
        int bottom = (int)Math.Ceiling(y + h);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Rows at the top of a whole-window capture (<paramref name="frameWidth"/> × <paramref name="frameHeight"/> backing
    /// pixels) that lie above the client area <paramref name="client"/> (points), i.e. the title bar in pixels: the
    /// frame is the window at <c>frameWidth / client.Width</c> pixels per point (the title bar does not change the
    /// width), so the client area is <c>client.Height</c> times that tall and the rest is title bar. 0 for a full-screen
    /// window. Clamped to [0, a quarter of the frame] so a rect from just before a resize cannot eat the picture.
    /// </summary>
    public static int TopInsetRows(int frameWidth, int frameHeight, PixelRect client)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || client.Width <= 0 || client.Height <= 0)
            return 0;
        double pixelsPerPoint = (double)frameWidth / client.Width;
        int rows = (int)Math.Round(frameHeight - client.Height * pixelsPerPoint);
        return Math.Clamp(rows, 0, frameHeight / 4);
    }
}
