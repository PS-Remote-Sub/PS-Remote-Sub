using System.Diagnostics;
using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;
using PSRemoteSub.Windows.Capture;

namespace PSRemoteSub.Windows.Tests;

[Collection(CaptureCardCollection.Name)]
public class CaptureCardEngineTests
{
    private const string CardId = "VID_345F&PID_2130";
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);
    private static readonly WindowInfo DummyWindow = new(0, 0, new PixelRect(0, 0, 0, 0), false);

    [CaptureCardFact]
    public async Task Opens1080p60_DeliversFrames()
    {
        using var engine = new CaptureCardEngine();
        engine.Open(await CardIdAsync(), null);

        Assert.True(WaitUntil(() => engine.State == CaptureState.Playing, OpenTimeout), $"State is {engine.State}");
        CaptureMode mode = Assert.IsType<CaptureMode>(engine.CurrentMode);
        Assert.Equal(1920, mode.Width);
        Assert.Equal(1080, mode.Height);
        Assert.InRange(mode.Fps, 59.5, 60.5);

        using var source = new CaptureCardFrameSource(engine);
        source.Start(DummyWindow);
        Frame frame = Assert.IsType<Frame>(source.Latest);
        Assert.Equal(1920, frame.Width);
        Assert.Equal(1080, frame.Height);
        // The card always sends a picture (its own "no signal" screen without a source), never all-zero memory.
        Assert.True(frame.Bgra.AsSpan().ContainsAnyExcept((byte)0), "The frame is all zero bytes (the GPU copy did nothing).");

        // Frames keep arriving at 60 fps, so retry until no frame lands between the two reads.
        for (int attempt = 0; attempt < 50; attempt++)
        {
            long before = Sequence(engine);
            Frame? first = source.Latest;
            Frame? second = source.Latest;
            if (Sequence(engine) == before)
            {
                Assert.NotNull(first);
                Assert.Same(first, second);
                return;
            }
        }
        Assert.Fail("A new frame arrived between every pair of reads.");
    }

    [CaptureCardFact]
    public async Task Opens1440p50WithinFiveSeconds()
    {
        using var engine = new CaptureCardEngine();
        engine.Open(await CardIdAsync(), new CaptureModeSetting(2560, 1440, 50));

        Assert.True(WaitUntil(() => engine.State == CaptureState.Playing, OpenTimeout), $"State is {engine.State}");
        CaptureMode mode = Assert.IsType<CaptureMode>(engine.CurrentMode);
        Assert.Equal(2560, mode.Width);
        Assert.Equal(1440, mode.Height);
    }

    [CaptureCardFact]
    public async Task Close_StopsFrames()
    {
        using var engine = new CaptureCardEngine();
        engine.Open(await CardIdAsync(), null);
        Assert.True(WaitUntil(() => engine.State == CaptureState.Playing, OpenTimeout), $"State is {engine.State}");

        var closing = Stopwatch.StartNew();
        engine.Close();
        Assert.True(closing.Elapsed < TimeSpan.FromSeconds(1), $"Close took {closing.Elapsed}");
        DateTimeOffset? afterClose = engine.LastFrameAt;
        Thread.Sleep(500);
        Assert.Equal(afterClose, engine.LastFrameAt);
    }

    [VideoDeviceFact]
    public void Close_WhileHandlerBlocks_DoesNotDeadlock()
    {
        // A handler that marshals synchronously to a busy UI thread (Dispatcher.Invoke) while that UI thread closes
        // the engine. No card needed: the first event (Opening) is raised before the device id is looked up.
        using var engine = new CaptureCardEngine();
        using var inHandler = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        engine.StateChanged += (_, state) =>
        {
            if (state != CaptureState.Opening)
                return;
            inHandler.Set();
            release.Wait(TimeSpan.FromSeconds(20));  // the "UI thread" is busy in Close
        };

        engine.Open(@"\\?\not-a-capture-device", null);
        Assert.True(inHandler.Wait(TimeSpan.FromSeconds(5)), "Opening was never raised.");

        var closing = new Thread(engine.Close) { IsBackground = true };
        closing.Start();
        bool closed = closing.Join(TimeSpan.FromSeconds(10));
        release.Set();
        Assert.True(closed, "Close blocked while a StateChanged handler was running.");
        Assert.Equal(CaptureState.NoDevice, engine.State);
    }

    private static long Sequence(CaptureCardEngine engine) => engine.UseLatest((_, _, sequence) => sequence);

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
}
