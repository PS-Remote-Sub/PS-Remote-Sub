using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Input;

namespace PSRemoteSub.Core.Platform;

/// <summary>
/// Everything the app needs from the operating system, in one place. The App picks one implementation at startup
/// (see <c>PlatformFactory</c>) and never checks the OS itself.
/// </summary>
public interface IPlatformServices
{
    PlatformKind Kind { get; }

    /// <summary>Folder for settings, captures, libraries and the log.</summary>
    string DataDir { get; }

    /// <summary>A watcher for the PlayStation Remote Play window, polled every <paramref name="pollInterval"/>.</summary>
    IWindowWatcher CreateRemotePlayWatcher(TimeSpan pollInterval);

    /// <summary>A frame source that captures the Remote Play window.</summary>
    IFrameSource CreateRemotePlayFrames();

    IOcrEngine Ocr { get; }

    IOcrLanguageInstaller OcrInstaller { get; }

    IOverlayWindowPlatform Overlay { get; }

    IKeepAwake KeepAwake { get; }

    IImageFileWriter ImageWriter { get; }

    ISecretProtector Secrets { get; }

    /// <summary>Per-port firewall rule setup; null when this OS has no such rule (Settings hides the firewall row).</summary>
    ILanAccessSetup? LanAccess { get; }

    IGlobalHotkeys CreateHotkeys();

    IPermissions Permissions { get; }

    /// <summary>
    /// The capture card, owned by the caller, or null when it cannot be used on this machine (the app then runs Remote
    /// Play only). Never throws.
    /// </summary>
    ICaptureCard? TryCreateCaptureCard();
}
