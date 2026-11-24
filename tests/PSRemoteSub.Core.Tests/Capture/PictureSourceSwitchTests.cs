using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Tests.Capture;

public class PictureSourceSwitchTests
{
    private static WindowInfo Win(nint handle) => new(handle, 1, new PixelRect(0, 0, 1920, 1080), false);

    private sealed class FakeWatcher : IWindowWatcher
    {
        public WindowInfo? Current { get; set; }
        public int Starts { get; private set; }
        public event EventHandler<WindowInfo?>? Changed;
        public void Start() => Starts++;
        public void Raise(WindowInfo? w) { Current = w; Changed?.Invoke(this, w); }
        public void Dispose() { }
    }

    private sealed class FakeFrames : IFrameSource
    {
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public Frame? Latest => null;
        public DateTimeOffset? LastFrameAt => null;
        public void Start(WindowInfo window) => Starts++;
        public void Stop() => Stops++;
        public void Dispose() { }
    }

    private readonly FakeWatcher _rpWatcher = new();
    private readonly FakeWatcher _cardWatcher = new();
    private readonly FakeFrames _rpFrames = new();
    private readonly FakeFrames _cardFrames = new();

    private PictureSourceSwitch Make(PictureSource initial = PictureSource.RemotePlay) =>
        new((_rpWatcher, _rpFrames), (_cardWatcher, _cardFrames), initial);

    [Fact]
    public void Select_RaisesChangedWithNewCurrent()
    {
        var card = Win(9);
        _cardWatcher.Current = card;
        using var sw = Make();
        var seen = new List<WindowInfo?>();
        sw.Watcher.Changed += (_, w) => seen.Add(w);

        sw.Select(PictureSource.CaptureCard);

        Assert.Equal(new WindowInfo?[] { card }, seen);
        Assert.Equal(card, sw.Watcher.Current);
        Assert.Equal(PictureSource.CaptureCard, sw.Selected);
    }

    [Fact]
    public void Changed_FromUnselectedWatcher_IsIgnored()
    {
        using var sw = Make();
        var seen = new List<WindowInfo?>();
        sw.Watcher.Changed += (_, w) => seen.Add(w);

        _cardWatcher.Raise(Win(9));
        Assert.Empty(seen);

        var rp = Win(1);
        _rpWatcher.Raise(rp);
        Assert.Equal(new WindowInfo?[] { rp }, seen);
    }

    [Fact]
    public void Select_StopsOldFrames_RoutesStartToNew()
    {
        using var sw = Make();

        sw.Select(PictureSource.CaptureCard);
        sw.Frames.Start(Win(9));

        Assert.Equal(1, _cardFrames.Starts);
        Assert.True(_rpFrames.Stops >= 1);
        Assert.Equal(0, _rpFrames.Starts);
    }

    [Fact]
    public void Select_SameSource_DoesNothing()
    {
        using var sw = Make();
        int raised = 0;
        sw.Watcher.Changed += (_, _) => raised++;

        sw.Select(PictureSource.RemotePlay);

        Assert.Equal(0, raised);
        Assert.Equal(0, _rpFrames.Stops);
    }

    [Fact]
    public void Start_StartsBothWatchers()
    {
        using var sw = Make();
        sw.Watcher.Start();
        Assert.Equal(1, _rpWatcher.Starts);
        Assert.Equal(1, _cardWatcher.Starts);
    }
}
