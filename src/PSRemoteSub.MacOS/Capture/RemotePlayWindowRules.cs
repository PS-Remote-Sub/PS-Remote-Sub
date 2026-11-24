using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.MacOS.Capture;

/// <summary>
/// Finds the PS Remote Play window among all windows and describes its client area (the game picture).
/// </summary>
/// <remarks>
/// PS Remote Play for Mac is <c>/Applications/RemotePlay.app</c>: bundle id <c>com.playstation.RemotePlay</c>,
/// executable and CFBundleName / CFBundleDisplayName <c>RemotePlay</c> (Info.plist of version 9.5.0), so
/// CGWindowListCopyWindowInfo reports its windows with owner name <c>RemotePlay</c>. The owner name is matched only when
/// no window carries the bundle id (for example if the bundle id cannot be read); <c>PS Remote Play</c> is also accepted
/// for older or renamed builds. Of the matching windows the largest on-screen one is the stream window. The title bar
/// height is not a constant: it comes from AppKit (<see cref="MacWindowList.TitleBarPoints"/>, 28 points for Remote
/// Play 9.5) and is 0 for a window that covers its whole display (full screen).
/// </remarks>
internal static class RemotePlayWindowRules
{
    public const string BundleId = "com.playstation.RemotePlay";

    private static readonly string[] OwnerNames = ["RemotePlay", "PS Remote Play"];

    /// <summary>
    /// The Remote Play window (by bundle id, else by owner name): the largest one on screen, or, when none is on
    /// screen (minimised, another Space), the largest one off screen; null if there is none. On-screen comes first
    /// because the window list also holds ordered-out (hidden) windows, which may be larger than the stream window.
    /// </summary>
    public static MacWindow? Choose(IEnumerable<MacWindow> windows)
    {
        var sized = windows.Where(w => w.Width > 0 && w.Height > 0).ToList();
        var byBundle = sized.Where(w => w.BundleId == BundleId).ToList();
        var candidates = byBundle.Count > 0 ? byBundle : sized.Where(w => OwnerNames.Contains(w.OwnerName, StringComparer.Ordinal)).ToList();
        return candidates.Where(w => w.OnScreen).MaxBy(w => w.Width * w.Height)
            ?? candidates.MaxBy(w => w.Width * w.Height);
    }

    /// <summary>True when the window exactly covers one display (within a point), as a full-screen window does.</summary>
    public static bool IsFullScreen(MacWindow window, IReadOnlyList<MacScreen> screens) =>
        screens.Any(s => Math.Abs(s.X - window.X) <= 1 && Math.Abs(s.Y - window.Y) <= 1
            && Math.Abs(s.Width - window.Width) <= 1 && Math.Abs(s.Height - window.Height) <= 1);

    /// <summary>
    /// The watcher's report for <paramref name="window"/>: its CGWindowID as the handle, the client area (window minus
    /// the title bar, unless full screen) in Avalonia screen units (<see cref="MacCoordinates"/>), and minimised when
    /// it is not on screen.
    /// </summary>
    public static WindowInfo ToWindowInfo(MacWindow window, IReadOnlyList<MacScreen> screens, double titleBarPoints)
    {
        double top = IsFullScreen(window, screens) ? 0 : Math.Clamp(titleBarPoints, 0, window.Height);
        return new WindowInfo(
            (nint)window.Id,
            window.OwnerPid,
            MacCoordinates.ToScreenPixels(window.X, window.Y + top, window.Width, window.Height - top),
            !window.OnScreen);
    }

    /// <summary>The window watcher's probe: the Remote Play window now, or null. Throws if the window list cannot be read.</summary>
    public static WindowInfo? Probe() =>
        Choose(MacWindowList.Windows()) is { } window
            ? ToWindowInfo(window, MacWindowList.Screens(), MacWindowList.TitleBarPoints)
            : null;
}
