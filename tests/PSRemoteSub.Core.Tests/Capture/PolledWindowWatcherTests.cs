using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.Core.Tests.Capture;

public class PolledWindowWatcherTests
{
    private static WindowInfo Win(nint handle) => new(handle, 1, new PixelRect(0, 0, 1920, 1080), false);

    [Fact]
    public void Refresh_RaisesOnlyOnChange()
    {
        var a = Win(1);
        var b = Win(2);
        var queue = new Queue<WindowInfo?>([a, a, b]);
        using var watcher = new PolledWindowWatcher(() => queue.Dequeue(), TimeSpan.FromSeconds(1));
        var seen = new List<WindowInfo?>();
        watcher.Changed += (_, w) => seen.Add(w);

        watcher.Refresh();
        watcher.Refresh();
        watcher.Refresh();

        Assert.Equal(new WindowInfo?[] { a, b }, seen);
        Assert.Equal(b, watcher.Current);
    }

    [Fact]
    public void Refresh_ProbeThrows_ReportsNull()
    {
        bool throwNow = false;
        using var watcher = new PolledWindowWatcher(
            () => throwNow ? throw new InvalidOperationException("boom") : Win(1), TimeSpan.FromSeconds(1));
        watcher.Refresh();
        var seen = new List<WindowInfo?>();
        watcher.Changed += (_, w) => seen.Add(w);

        throwNow = true;
        watcher.Refresh();

        Assert.Equal(new WindowInfo?[] { null }, seen);
        Assert.Null(watcher.Current);
    }

    [Fact]
    public async Task SetProbe_WhileOldProbeRuns_NewResultWins()
    {
        var a = Win(1);
        var b = Win(2);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var swapStarted = new ManualResetEventSlim();
        using var watcher = new PolledWindowWatcher(() =>
        {
            entered.Set();
            release.Wait();
            return a;
        }, TimeSpan.FromSeconds(1));
        var seen = new List<WindowInfo?>();
        watcher.Changed += (_, w) => { lock (seen) seen.Add(w); };

        var first = Task.Run(watcher.Refresh);
        entered.Wait();
        var swap = Task.Run(() =>
        {
            swapStarted.Set();
            watcher.SetProbe(() => b);
        });
        swapStarted.Wait();
        release.Set();
        await Task.WhenAll(first, swap);

        Assert.Equal(b, watcher.Current);
        Assert.Equal(b, seen[^1]);
    }

    [Fact]
    public void SetProbe_RefreshesAtOnce()
    {
        using var watcher = new PolledWindowWatcher(() => Win(1), TimeSpan.FromSeconds(1));
        watcher.Refresh();
        var seen = new List<WindowInfo?>();
        watcher.Changed += (_, w) => seen.Add(w);

        watcher.SetProbe(() => null);

        Assert.Equal(new WindowInfo?[] { null }, seen);
    }
}
