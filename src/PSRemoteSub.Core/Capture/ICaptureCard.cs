using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Capture;

/// <summary>A key the video surface reports while it has focus: F11 and Esc on Windows and Mac.</summary>
public enum VideoSurfaceKey { ToggleFullScreen, LeaveFullScreen }

/// <summary>
/// A native view that shows the capture card's picture, hosted in the capture window. Created by
/// <see cref="ICaptureCard.CreateSurface"/> on the UI thread, which must also dispose it; its events are raised there.
/// </summary>
/// <remarks>Disposing it first stops drawing into it, then destroys the native view.</remarks>
public interface IVideoSurface : IDisposable
{
    /// <summary>The native view: an HWND on Windows, an <c>NSView*</c> on Mac. 0 once it is gone.</summary>
    nint Handle { get; }

    /// <summary>The handle's kind for Avalonia's <c>PlatformHandle</c>: "HWND" or "NSView".</summary>
    string HandleDescriptor { get; }

    /// <summary>A left-button double-click on the picture.</summary>
    event EventHandler? DoubleClicked;

    /// <summary>A key the surface handles itself, pressed while it has focus.</summary>
    event EventHandler<VideoSurfaceKey>? KeyPressed;
}

/// <summary>Plays the capture card's audio input on the default output device.</summary>
/// <remarks>
/// Every member is safe to call from any thread. <see cref="RunningChanged"/> may be raised on any thread, only when
/// <see cref="IsRunning"/> changes.
/// </remarks>
public interface ICaptureAudio : IDisposable
{
    bool IsRunning { get; }

    event EventHandler<bool>? RunningChanged;

    /// <summary>Output volume from 0 to 1; values outside the range are clamped.</summary>
    double Volume { get; set; }

    bool Muted { get; set; }

    /// <summary>Starts (or restarts) playing the capture endpoint <paramref name="audioDeviceId"/>.</summary>
    void Start(string audioDeviceId);

    /// <summary>Stops playback. <see cref="IsRunning"/> is false when this returns.</summary>
    void Stop();
}

/// <summary>
/// The capture card on this OS: opens a device, keeps its newest frame for the OCR pipeline (<see cref="Frames"/>) and
/// presents its picture into video surfaces. Owned by the AppHost; the capture window drives it.
/// </summary>
/// <remarks>
/// <see cref="StateChanged"/> may be raised on any thread; a handler must not call <see cref="Open"/>,
/// <see cref="Close"/> or <see cref="Dispose"/> synchronously. Dispose every surface and the frame consumer's use of
/// <see cref="Frames"/> before the card when you can; the app cannot always (a surface goes on the UI thread later), so
/// <see cref="IDisposable.Dispose"/> stops presenting into any surface still alive, which may then only be disposed.
/// </remarks>
public interface ICaptureCard : IDisposable
{
    CaptureState State { get; }

    /// <summary>Raised when <see cref="State"/> changes, and only then.</summary>
    event EventHandler<CaptureState>? StateChanged;

    /// <summary>The mode being read, or null when nothing is open.</summary>
    CaptureMode? CurrentMode { get; }

    /// <summary>The native modes of the device last opened.</summary>
    IReadOnlyList<CaptureMode> OfferedModes { get; }

    DateTimeOffset? LastFrameAt { get; }

    /// <summary>The card's frames for the OCR pipeline (owned by the card).</summary>
    IFrameSource Frames { get; }

    /// <summary>Opens <paramref name="deviceId"/> (closing what was open). Returns at once.</summary>
    void Open(string deviceId, CaptureModeSetting? wanted);

    /// <summary>Stops reading and releases the device so another app can open it.</summary>
    void Close();

    /// <summary>Lists the capture devices with their paired audio input. Opens no device.</summary>
    Task<IReadOnlyList<CaptureDevice>> ListDevicesAsync();

    /// <summary>Lists the native video modes of <paramref name="deviceId"/>. Blocking: call it off the UI thread.</summary>
    IReadOnlyList<CaptureMode> ListModes(string deviceId);

    /// <summary>A new audio player for the card's audio input (owned by the caller).</summary>
    ICaptureAudio CreateAudio();

    /// <summary>
    /// Creates a surface as a child of the native view <paramref name="parentHandle"/> and presents the card's picture
    /// into it until it is disposed. UI thread only.
    /// </summary>
    IVideoSurface CreateSurface(nint parentHandle);

    /// <summary>
    /// Where the picture is on screen inside <paramref name="surface"/> (letterboxed to <see cref="CurrentMode"/>),
    /// reported as a window of <paramref name="topLevel"/>; null when the surface is gone. Any thread.
    /// </summary>
    WindowInfo? MeasurePicture(nint topLevel, IVideoSurface surface);
}
