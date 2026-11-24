using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Input;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Windows.Capture;
using PSRemoteSub.Windows.Imaging;
using PSRemoteSub.Windows.Ocr;
using PSRemoteSub.Windows.Security;
using PSRemoteSub.Windows.Shell;

namespace PSRemoteSub.Windows;

/// <summary>The Windows implementation of <see cref="IPlatformServices"/>: the Win32 / WinRT classes of this project.</summary>
public sealed class WindowsPlatformServices : IPlatformServices
{
    public PlatformKind Kind => PlatformKind.Windows;

    public string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PSRemoteSub");

    public IWindowWatcher CreateRemotePlayWatcher(TimeSpan pollInterval) => new Win32WindowWatcher(pollInterval);

    public IFrameSource CreateRemotePlayFrames() => new WgcFrameSource();

    public IOcrEngine Ocr { get; } = new WindowsOcrEngine();

    public IOcrLanguageInstaller OcrInstaller { get; } = new CapabilityOcrInstaller();

    public IOverlayWindowPlatform Overlay { get; } = new Win32OverlayPlatform();

    public IKeepAwake KeepAwake { get; } = new Win32KeepAwake();

    public IImageFileWriter ImageWriter { get; } = new WinRtImageFileWriter();

    public ISecretProtector Secrets { get; } = new DpapiSecretProtector();

    public ILanAccessSetup? LanAccess { get; } = new NetshLanAccessSetup();

    public IGlobalHotkeys CreateHotkeys() => new Win32GlobalHotkeys();

    public IPermissions Permissions { get; } = new AlwaysGrantedPermissions();

    /// <summary>
    /// The capture card, or null when it cannot be built here (Media Foundation missing, as on Windows N without the
    /// Media Feature Pack, or no hardware Direct3D 11 device with video support). Remote Play must start regardless.
    /// </summary>
    public ICaptureCard? TryCreateCaptureCard()
    {
        try
        {
            return new WindowsCaptureCard();
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Log.Warn("Capture card unavailable: its engine could not be created", ex);
            return null;
        }
    }

    /// <summary>Windows has no per-app permission prompts for what the app uses: everything counts as granted.</summary>
    private sealed class AlwaysGrantedPermissions : IPermissions
    {
        public PermissionState Check(PlatformPermission p) => PermissionState.Granted;

        public Task<bool> RequestAsync(PlatformPermission p) => Task.FromResult(true);

        public void OpenSettings(PlatformPermission p) { }
    }
}
