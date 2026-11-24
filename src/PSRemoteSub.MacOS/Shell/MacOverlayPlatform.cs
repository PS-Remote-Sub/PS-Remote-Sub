using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.MacOS.Shell;

/// <summary>Makes an Avalonia window a click-through overlay that never takes keyboard focus (see Overlay.swift).</summary>
public sealed class MacOverlayPlatform : IOverlayWindowPlatform
{
    public void ConfigureOverlay(nint windowHandle)
    {
        if (windowHandle == 0) return;
        try
        {
            int code = Native.psrs_overlay_configure(windowHandle);
            if (code != 0) Log.Warn($"Overlay configure failed (code {code})");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn("Overlay configure unavailable", ex);
        }
    }
}
