using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Input;
using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.MacOS.Capture;
using PSRemoteSub.MacOS.Imaging;
using PSRemoteSub.MacOS.Input;
using PSRemoteSub.MacOS.Ocr;
using PSRemoteSub.MacOS.Security;
using PSRemoteSub.MacOS.Shell;

namespace PSRemoteSub.MacOS;

/// <summary>
/// The macOS implementation of <see cref="IPlatformServices"/>: ScreenCaptureKit and AVFoundation capture, Vision OCR,
/// Carbon hotkeys, an AppKit overlay and TCC permissions through the native dylib. <see cref="LanAccess"/> is null: macOS
/// has no firewall rule to set up (Local Network access is a TCC permission).
/// </summary>
public sealed class MacPlatformServices : IPlatformServices
{
    public PlatformKind Kind => PlatformKind.MacOS;

    public string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "PSRemoteSub");

    public MacPlatformServices() => Secrets = new AesKeyFileSecretProtector(Path.Combine(DataDir, "secret.key"));

    /// <summary>Mac has no window move/resize hooks, so the Remote Play probe polls at least this often.</summary>
    private static readonly TimeSpan MaxRemotePlayPoll = TimeSpan.FromMilliseconds(250);

    private PolledWindowWatcher? _remotePlayWatcher;

    public IWindowWatcher CreateRemotePlayWatcher(TimeSpan pollInterval)
    {
        var watcher = new PolledWindowWatcher(RemotePlayWindowRules.Probe, pollInterval < MaxRemotePlayPoll ? pollInterval : MaxRemotePlayPoll);
        WindowInfo? logged = null;
        watcher.Changed += (_, info) =>
        {
            // Not every move (a drag reports every poll): only a new window, a new size or minimise/restore.
            if (info is not null && logged is not null && info.Handle == logged.Handle && info.IsMinimized == logged.IsMinimized
                && info.ClientScreenRect.Width == logged.ClientScreenRect.Width && info.ClientScreenRect.Height == logged.ClientScreenRect.Height)
                return;
            logged = info;
            if (info is not null)
                Log.Info($"Remote Play window {info.Handle} (pid {info.ProcessId}): client {info.ClientScreenRect}{(info.IsMinimized ? ", minimised" : "")}");
        };
        Volatile.Write(ref _remotePlayWatcher, watcher);
        return watcher;
    }

    /// <summary>Captures the window the pipeline starts it on; crops the title bar using the watcher's latest rect.</summary>
    public IFrameSource CreateRemotePlayFrames() => new ScreenCaptureFrameSource(() => Volatile.Read(ref _remotePlayWatcher)?.Current);

    public IOcrEngine Ocr { get; } = new VisionOcrEngine();

    public IOcrLanguageInstaller OcrInstaller { get; } = new BuiltInOcrLanguages();

    public IOverlayWindowPlatform Overlay { get; } = new MacOverlayPlatform();

    public IKeepAwake KeepAwake { get; } = new MacKeepAwake();

    public IImageFileWriter ImageWriter { get; } = new ImageIoFileWriter();

    public ISecretProtector Secrets { get; }

    public ILanAccessSetup? LanAccess => null;

    public IGlobalHotkeys CreateHotkeys() => new MacGlobalHotkeys();

    public IPermissions Permissions { get; } = new MacPermissions();

    /// <summary>
    /// The AVFoundation capture card. Never null: creating it touches no device, and missing camera or microphone access
    /// shows as the card's own states.
    /// </summary>
    public ICaptureCard? TryCreateCaptureCard() => new AvCaptureCard();
}
