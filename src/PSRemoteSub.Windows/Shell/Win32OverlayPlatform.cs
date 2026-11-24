using PSRemoteSub.Core.Platform;
using PSRemoteSub.Windows.Shell.Interop;

namespace PSRemoteSub.Windows.Shell;

/// <summary>Makes a window a click-through, non-activating tool window.</summary>
public sealed class Win32OverlayPlatform : IOverlayWindowPlatform
{
    public void ConfigureOverlay(nint windowHandle)
    {
        if (windowHandle == 0) return;
        nint style = ShellNative.GetWindowLongPtr(windowHandle, ShellNative.GWL_EXSTYLE);
        style |= ShellNative.WS_EX_LAYERED | ShellNative.WS_EX_TRANSPARENT
               | ShellNative.WS_EX_TOOLWINDOW | ShellNative.WS_EX_NOACTIVATE;
        ShellNative.SetWindowLongPtr(windowHandle, ShellNative.GWL_EXSTYLE, style);
    }
}
