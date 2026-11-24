using PSRemoteSub.Core.Capture;

namespace PSRemoteSub.MacOS.Capture;

/// <summary>
/// What the AVFoundation capture session reported (the values are the native ones, CaptureCard.swift).
/// <see cref="DeviceInUse"/> is an interruption, which AVFoundation ends by itself; <see cref="DeviceInUseError"/> is
/// the device being in use when opened (or a runtime error saying so), after which the session is dead.
/// </summary>
internal enum AvEvent { Opening, Running, NoFrames3s, DeviceInUse, NotAuthorized, Disconnected, RuntimeError, DeviceInUseError }

internal static class AvStateMapping
{
    public static CaptureState ToState(AvEvent e) => e switch
    {
        AvEvent.Opening => CaptureState.Opening,
        AvEvent.Running => CaptureState.Playing,
        AvEvent.NoFrames3s => CaptureState.NoSignal,
        AvEvent.DeviceInUse or AvEvent.DeviceInUseError => CaptureState.InUse,
        AvEvent.NotAuthorized => CaptureState.Blocked,
        AvEvent.Disconnected => CaptureState.Unplugged,
        AvEvent.RuntimeError => CaptureState.Failed,
        _ => CaptureState.Failed,
    };

    /// <summary>
    /// Whether the session is finished after <paramref name="e"/> (it reports nothing more), so the card closes it and
    /// opens the device again in 3 s, as the Windows engine retries a failure. Not for an interruption, which
    /// AVFoundation resumes itself.
    /// </summary>
    public static bool Reopens(AvEvent e) =>
        e is AvEvent.Disconnected or AvEvent.RuntimeError or AvEvent.NotAuthorized or AvEvent.DeviceInUseError;
}
