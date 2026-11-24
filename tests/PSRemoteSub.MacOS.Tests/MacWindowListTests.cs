using PSRemoteSub.MacOS.Capture;

namespace PSRemoteSub.MacOS.Tests;

public class MacWindowListTests
{
    [MacFact]
    public void Windows_AreLayerZeroWithOwners()
    {
        var windows = MacWindowList.Windows();
        Assert.All(windows, w =>
        {
            Assert.Equal(0, w.Layer);
            Assert.NotEqual(0, w.OwnerPid);
            Assert.False(string.IsNullOrEmpty(w.OwnerName));
        });
    }

    [MacFact]
    public void Screens_MainDisplayAtOrigin()
    {
        var screens = MacWindowList.Screens();
        Assert.NotEmpty(screens);
        Assert.Contains(screens, s => s.X == 0 && s.Y == 0);
        Assert.All(screens, s => Assert.True(s.Width > 0 && s.Height > 0 && s.Scale >= 1, s.ToString()));
    }

    [MacFact]
    public void TitleBarPoints_IsAStandardTitleBar() => Assert.InRange(MacWindowList.TitleBarPoints, 20, 60);
}
