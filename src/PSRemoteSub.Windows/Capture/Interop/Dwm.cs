using System.Runtime.InteropServices;

namespace PSRemoteSub.Windows.Capture.Interop;

internal static partial class Dwm
{
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out RECT value, int size);

    /// <summary>Visible window bounds in physical screen pixels (the area WGC captures for a window).</summary>
    public static bool TryGetExtendedFrameBounds(nint hwnd, out RECT bounds) =>
        DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out bounds, Marshal.SizeOf<RECT>()) >= 0;
}
