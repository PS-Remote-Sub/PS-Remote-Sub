using System.Globalization;
using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App;

/// <summary>Texts for the capture card: shared by the capture window, the settings panel and the status line.</summary>
internal static class CaptureText
{
    /// <summary>"อัตโนมัติ" for null, otherwise "1920×1080 @ 60".</summary>
    internal static string ModeLabel(CaptureModeSetting? mode) => mode is null
        ? Loc.Get("CaptureModeAuto")
        : string.Format(CultureInfo.InvariantCulture, "{0}×{1} @ {2:0.##}", mode.Width, mode.Height, mode.Fps);

    /// <summary>The Strings key of the message shown for <paramref name="state"/> (also the status line while it shows).</summary>
    internal static string MessageKey(CaptureState state) => state switch
    {
        CaptureState.NoDevice => "CaptureNoDevice",
        CaptureState.Opening => "CaptureOpening",
        CaptureState.NoSignal => "CaptureNoSignal",
        CaptureState.InUse => "CaptureInUse",
        CaptureState.Blocked => "CaptureBlocked",
        CaptureState.Unplugged => "CaptureUnplugged",
        CaptureState.Failed => "CaptureFailed",
        _ => "CaptureOpening",
    };
}
