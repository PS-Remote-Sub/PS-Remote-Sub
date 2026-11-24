using PSRemoteSub.Windows.Capture;

namespace PSRemoteSub.Windows.Tests;

public class RemotePlayWindowChooserTests
{
    private static WindowCandidate W(nint h, bool visible = true, bool owned = false, bool minimized = false, int w = 100, int ht = 100) =>
        new(h, visible, owned, minimized, w, ht);

    [Fact]
    public void HiddenLauncher_IsSkipped_ForVisibleStreamWindow()
    {
        // Seen on a real machine: the console-select window stays alive but hidden once streaming starts.
        var launcher = W(984820, visible: false, w: 1080, ht: 540);
        var stream = W(198854, w: 2560, ht: 1351);

        Assert.Equal((nint)198854, RemotePlayWindowChooser.Choose([launcher, stream], current: 984820));
    }

    [Fact]
    public void OwnedToolWindows_AreIgnored()
    {
        var toolbar = W(67788, owned: true, w: 2560, ht: 120);
        var stream = W(198854, w: 1280, ht: 720);

        Assert.Equal((nint)198854, RemotePlayWindowChooser.Choose([toolbar, stream], current: 0));
    }

    [Fact]
    public void CurrentVisibleWindow_IsKept_EvenIfAnotherIsLarger()
    {
        var current = W(1, w: 800, ht: 450);
        var other = W(2, w: 1920, ht: 1080);

        Assert.Equal((nint)1, RemotePlayWindowChooser.Choose([current, other], current: 1));
    }

    [Fact]
    public void NoCurrent_PrefersRestoredThenLargest()
    {
        var minimizedBig = W(1, minimized: true, w: 2560, ht: 1440);
        var small = W(2, w: 800, ht: 450);
        var large = W(3, w: 1600, ht: 900);

        Assert.Equal((nint)3, RemotePlayWindowChooser.Choose([minimizedBig, small, large], current: 0));
    }

    [Fact]
    public void OnlyMinimizedVisibleWindow_IsStillChosen()
    {
        // Minimized windows are visible; the pipeline reports "minimized" instead of "not found".
        Assert.Equal((nint)5, RemotePlayWindowChooser.Choose([W(5, minimized: true)], current: 0));
    }

    [Fact]
    public void NoVisibleUnownedWindow_ReturnsZero()
    {
        Assert.Equal((nint)0, RemotePlayWindowChooser.Choose([W(1, visible: false), W(2, owned: true)], current: 1));
    }
}
