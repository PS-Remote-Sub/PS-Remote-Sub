using PSRemoteSub.Core.Capture;
using PSRemoteSub.Windows.Capture;

namespace PSRemoteSub.Windows.Tests;

public class WindowsVideoSurfaceTests
{
    [Fact]
    public void MapsKeys()
    {
        Assert.Equal(VideoSurfaceKey.ToggleFullScreen, WindowsVideoSurface.MapKey(0x7A)); // VK_F11
        Assert.Equal(VideoSurfaceKey.LeaveFullScreen, WindowsVideoSurface.MapKey(0x1B));  // VK_ESCAPE
        Assert.Null(WindowsVideoSurface.MapKey(0x41));                                    // 'A'
    }
}
