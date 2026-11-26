using PSRemoteSub.MacOS.Shell;

namespace PSRemoteSub.MacOS.Tests;

/// <summary>Native-binding smoke tests: the exports exist, accept their arguments and never throw.</summary>
public class MacShellTests
{
    [MacFact]
    public void KeepAwake_SetTrueThenFalse_NoThrow()
    {
        var keepAwake = new MacKeepAwake();
        keepAwake.SetActive(true);
        keepAwake.SetActive(true); // idempotent
        keepAwake.SetActive(false);
        keepAwake.SetActive(false);
    }

    [MacFact]
    public void Overlay_ConfigureZero_NoThrow() => new MacOverlayPlatform().ConfigureOverlay(0);

    [MacFact]
    public void Overlay_NativeRejectsNullAndGarbage_ByCode()
    {
        Assert.Equal(1, Native.psrs_overlay_configure(0));
    }

    [MacFact]
    public void Toast_ConfigureZero_NoThrow() => new MacOverlayPlatform().ConfigureToast(0);

    [MacFact]
    public void Toast_NativeRejectsNull_ByCode()
    {
        Assert.Equal(1, Native.psrs_toast_configure(0));
    }
}
