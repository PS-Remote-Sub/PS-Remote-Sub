using System.ComponentModel;
using System.Diagnostics;
using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Windows.Capture;
using PSRemoteSub.Windows.Capture.Interop;

namespace PSRemoteSub.Windows.Tests;

[Collection(CaptureCardCollection.Name)]
public class VideoPresenterTests
{
    private const string CardId = "VID_345F&PID_2130";
    private const uint WS_POPUP = 0x80000000;

    [Fact]
    public void Measure_ZeroSurface_ReturnsNull()
    {
        Assert.Null(CaptureSurfaceProbe.Measure(0, 0, (1920, 1080)));
    }

    [Fact]
    public void Measure_ChildOfTestWindow_ReturnsLetterboxedRect()
    {
        using var host = TestWindow.Create(1000, 1000);
        using var surface = VideoSurfaceWindow.Create(host.Handle);

        WindowInfo info = Assert.IsType<WindowInfo>(CaptureSurfaceProbe.Measure(host.Handle, surface.Handle, (1920, 1080)));

        Assert.Equal(host.Handle, info.Handle);
        Assert.Equal(Environment.ProcessId, info.ProcessId);
        Assert.Equal(1000, info.ClientScreenRect.Width);
        Assert.Equal(563, info.ClientScreenRect.Height);
        Assert.False(info.IsMinimized);
    }

    [Fact]
    public void Measure_NoFrameSize_ReturnsWholeSurface()
    {
        using var host = TestWindow.Create(800, 600);
        using var surface = VideoSurfaceWindow.Create(host.Handle);

        WindowInfo info = Assert.IsType<WindowInfo>(CaptureSurfaceProbe.Measure(host.Handle, surface.Handle, null));

        Assert.Equal(800, info.ClientScreenRect.Width);
        Assert.Equal(600, info.ClientScreenRect.Height);
    }

    [Fact]
    public void Measure_DestroyedSurface_ReturnsNull()
    {
        using var host = TestWindow.Create(800, 600);
        var surface = VideoSurfaceWindow.Create(host.Handle);
        nint handle = surface.Handle;
        surface.Dispose();

        Assert.Null(CaptureSurfaceProbe.Measure(host.Handle, handle, (1920, 1080)));
    }

    [CaptureCardFact]
    public async Task Presenter_RunsWithoutFaults()
    {
        string cardId = await CardIdAsync();
        // Everything below stays on this thread (no more awaits): the windows belong to it.
        using var engine = new CaptureCardEngine();
        engine.Open(cardId, null);
        Assert.True(WaitUntil(() => engine.State == CaptureState.Playing, TimeSpan.FromSeconds(5)), $"State is {engine.State}");

        using var host = TestWindow.Create(1000, 1000);
        using var surface = VideoSurfaceWindow.Create(host.Handle);

        // Read the back buffer back once, after the first blt, to prove the presenter draws the picture.
        Frame? drawn = null;
        int rendered = 0;
        var presenter = new VideoPresenter(engine, surface.Handle, (context, backBuffer) =>
        {
            if (Interlocked.Increment(ref rendered) != 1)
                return;
            using var readback = new TextureReadback(engine.Device);
            Volatile.Write(ref drawn, readback.Read(context, backBuffer));
        });

        // No message pump on purpose: the presenter must not depend on the window's thread (the UI thread may be
        // blocked in Dispose while the presenter is mid-present).
        Thread.Sleep(2000);
        var disposing = Stopwatch.StartNew();
        presenter.Dispose();

        Assert.True(disposing.Elapsed < TimeSpan.FromSeconds(1), $"Dispose took {disposing.Elapsed}");
        Assert.Equal(0, presenter.FailureCount);
        Assert.Equal(CaptureState.Playing, engine.State);
        Frame frame = Assert.IsType<Frame>(Volatile.Read(ref drawn));
        Assert.Equal(1000, frame.Width);
        Assert.Equal(1000, frame.Height);
        // 1920x1080 in 1000x1000 leaves 1000x563 of picture between black bars.
        Assert.True(IsBlack(frame, 500, 10), "The top bar is not black.");
        Assert.True(CountNonBlack(frame) > 0, "The presenter drew only black pixels.");
        Assert.True(Volatile.Read(ref rendered) > 30, $"Only {rendered} frames were rendered in 2 s.");    }

    [VideoDeviceFact]
    public void Presenter_WithoutFrames_DrawsBlackOnce()
    {
        using var engine = new CaptureCardEngine();  // never opened: no frames
        using var host = TestWindow.Create(320, 240);
        using var surface = VideoSurfaceWindow.Create(host.Handle);
        Frame? drawn = null;
        int rendered = 0;
        using var presenter = new VideoPresenter(engine, surface.Handle, (context, backBuffer) =>
        {
            if (Interlocked.Increment(ref rendered) != 1)
                return;
            using var readback = new TextureReadback(engine.Device);
            Volatile.Write(ref drawn, readback.Read(context, backBuffer));
        });

        Assert.True(WaitUntil(() => Volatile.Read(ref rendered) > 0, TimeSpan.FromSeconds(2)), $"Nothing was drawn ({presenter.FailureCount} failures).");
        Thread.Sleep(500);

        Assert.Equal(1, Volatile.Read(ref rendered));
        Frame frame = Assert.IsType<Frame>(Volatile.Read(ref drawn));
        Assert.Equal(0, CountNonBlack(frame));
        Assert.Equal(0, presenter.FailureCount);
    }

    [VideoDeviceFact]
    public void Presenter_EngineDisposedFirst_StopsQuietly()
    {
        var engine = new CaptureCardEngine();
        using var host = TestWindow.Create(320, 240);
        using var surface = VideoSurfaceWindow.Create(host.Handle);
        int rendered = 0;
        var presenter = new VideoPresenter(engine, surface.Handle, (_, _) => Interlocked.Increment(ref rendered));
        Assert.True(WaitUntil(() => Volatile.Read(ref rendered) > 0, TimeSpan.FromSeconds(2)), $"Nothing was drawn ({presenter.FailureCount} failures).");

        engine.Dispose();
        Thread.Sleep(300);
        var disposing = Stopwatch.StartNew();
        presenter.Dispose();

        Assert.True(disposing.Elapsed < TimeSpan.FromMilliseconds(500), $"Dispose took {disposing.Elapsed}");
        Assert.Equal(0, presenter.FailureCount);
    }

    [CaptureCardFact]
    public async Task Presenter_FollowsSurfaceSize_AndIdlesAtZeroSize()
    {
        string cardId = await CardIdAsync();
        using var engine = new CaptureCardEngine();
        engine.Open(cardId, null);
        Assert.True(WaitUntil(() => engine.State == CaptureState.Playing, TimeSpan.FromSeconds(5)), $"State is {engine.State}");

        using var host = TestWindow.Create(1000, 1000);
        using var surface = VideoSurfaceWindow.Create(host.Handle);
        int rendered = 0;
        long lastSize = 0;  // width << 32 | height of the last back buffer drawn
        using var presenter = new VideoPresenter(engine, surface.Handle, (_, backBuffer) =>
        {
            var d = backBuffer.Description;
            Interlocked.Exchange(ref lastSize, (long)d.Width << 32 | d.Height);
            Interlocked.Increment(ref rendered);
        });
        Assert.True(WaitUntil(() => Interlocked.Read(ref lastSize) == (1000L << 32 | 1000), TimeSpan.FromSeconds(2)), "Never drew at 1000x1000.");

        User32.MoveWindow(surface.Handle, 0, 0, 640, 360, false);
        Assert.True(WaitUntil(() => Interlocked.Read(ref lastSize) == (640L << 32 | 360), TimeSpan.FromSeconds(2)), "Never drew at 640x360 after the resize.");

        User32.MoveWindow(surface.Handle, 0, 0, 0, 0, false);
        Thread.Sleep(300);  // let a frame in flight finish
        int atZero = Volatile.Read(ref rendered);
        Thread.Sleep(500);
        Assert.Equal(atZero, Volatile.Read(ref rendered));

        User32.MoveWindow(surface.Handle, 0, 0, 800, 600, false);
        Assert.True(WaitUntil(() => Interlocked.Read(ref lastSize) == (800L << 32 | 600), TimeSpan.FromSeconds(2)), "Never drew again after the 0x0 surface came back.");

        User32.ShowWindow(host.Handle, User32.SW_SHOWMINNOACTIVE);
        Assert.True(User32.IsIconic(host.Handle));
        Thread.Sleep(300);
        int minimised = Volatile.Read(ref rendered);
        Thread.Sleep(500);
        Assert.Equal(minimised, Volatile.Read(ref rendered));
        Assert.Equal(0, presenter.FailureCount);
    }

    private static bool IsBlack(Frame frame, int x, int y)
    {
        int i = (y * frame.Width + x) * 4;
        return frame.Bgra[i] == 0 && frame.Bgra[i + 1] == 0 && frame.Bgra[i + 2] == 0;
    }

    private static int CountNonBlack(Frame frame)
    {
        int count = 0;
        for (int i = 0; i < frame.Bgra.Length; i += 4)
        {
            if (frame.Bgra[i] != 0 || frame.Bgra[i + 1] != 0 || frame.Bgra[i + 2] != 0)
                count++;
        }
        return count;
    }

    private static async Task<string> CardIdAsync()
    {
        var devices = await CaptureDeviceCatalog.ListAsync();
        return devices.First(d => d.Id.Contains(CardId, StringComparison.OrdinalIgnoreCase)).Id;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (condition())
                return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    /// <summary>A hidden borderless top-level window (client area = window size) on the calling thread.</summary>
    private sealed class TestWindow : IDisposable
    {
        private TestWindow(nint handle) => Handle = handle;

        public nint Handle { get; }

        public static TestWindow Create(int width, int height)
        {
            nint handle = User32.CreateWindowExW(0, "STATIC", null, WS_POPUP, 0, 0, width, height, 0, 0, 0, 0);
            if (handle == 0)
                throw new Win32Exception();
            return new TestWindow(handle);
        }

        public void Dispose() => User32.DestroyWindow(Handle);
    }
}
