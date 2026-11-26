namespace PSRemoteSub.Core.Platform;

public interface IOverlayWindowPlatform
{
    /// <summary>Makes the window a topmost, click-through overlay that never takes focus.</summary>
    void ConfigureOverlay(nint windowHandle);

    /// <summary>
    /// Makes the window a topmost toast that is shown without taking focus but still takes clicks (its buttons).
    /// </summary>
    void ConfigureToast(nint windowHandle);
}
