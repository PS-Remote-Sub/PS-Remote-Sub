namespace PSRemoteSub.Core.Platform;

public interface IOverlayWindowPlatform
{
    void ConfigureOverlay(nint windowHandle);
}
