using System.Runtime.InteropServices;
using System.Text.Json;

namespace PSRemoteSub.MacOS.Capture;

/// <summary>
/// A normal (layer 0) window as CGWindowListCopyWindowInfo reports it. Bounds are Quartz global points: origin at the
/// top-left of the main display, Y down (other displays may have negative coordinates). <see cref="Id"/> is the
/// CGWindowID; <see cref="OnScreen"/> is false for minimised windows and windows on another Space.
/// </summary>
public sealed record MacWindow(
    uint Id, int OwnerPid, string OwnerName, string? BundleId, double X, double Y, double Width, double Height, int Layer, bool OnScreen);

/// <summary>An active display: bounds in Quartz global points (main display at 0,0), <see cref="Scale"/> backing pixels per point.</summary>
internal sealed record MacScreen(double X, double Y, double Width, double Height, double Scale);

/// <summary>Windows, displays and the title bar height, from libPSRemoteSubMac (CoreGraphics; safe on any thread).</summary>
internal static class MacWindowList
{
    private sealed record WindowDto(uint Id, int Pid, string? Owner, string? Bundle, double X, double Y, double W, double H, int Layer, bool OnScreen);

    private sealed record ScreenDto(double X, double Y, double W, double H, double Scale);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static readonly Lazy<double> TitleBar = new(Native.psrs_title_bar_points);

    /// <summary>
    /// Title bar height in points of a standard titled window, from <c>NSWindow.frameRect(forContentRect:styleMask:
    /// [.titled])</c> in this process. Not a constant: AppKit picks it by the SDK an app was linked against — 28 for
    /// pre-macOS 26 SDKs, 32 for newer ones on the development Mac (macOS 27). This app runs in the .NET apphost (SDK
    /// 15.5) and PS Remote Play 9.5 is linked against SDK 14.5, so both get 28.
    /// </summary>
    public static double TitleBarPoints => TitleBar.Value;

    /// <summary>Every layer-0 window, front to back. Throws <see cref="InvalidOperationException"/> if the list cannot be read.</summary>
    public static IReadOnlyList<MacWindow> Windows() =>
        Read<WindowDto>(Native.psrs_windows_list, "window list")
            .Select(w => new MacWindow(w.Id, w.Pid, w.Owner ?? "", w.Bundle, w.X, w.Y, w.W, w.H, w.Layer, w.OnScreen))
            .ToList();

    /// <summary>The active displays. Throws <see cref="InvalidOperationException"/> if they cannot be read.</summary>
    public static IReadOnlyList<MacScreen> Screens() =>
        Read<ScreenDto>(Native.psrs_screens, "screen list").Select(s => new MacScreen(s.X, s.Y, s.W, s.H, s.Scale)).ToList();

    private delegate int JsonCall(out nint json);

    private static List<T> Read<T>(JsonCall call, string what)
    {
        int rc = call(out nint json);
        if (rc != 0)
            throw new InvalidOperationException($"Reading the {what} failed (code {rc}).");
        try
        {
            return JsonSerializer.Deserialize<List<T>>(Marshal.PtrToStringUTF8(json) ?? "[]", Json) ?? [];
        }
        finally
        {
            Native.psrs_free(json);
        }
    }
}
