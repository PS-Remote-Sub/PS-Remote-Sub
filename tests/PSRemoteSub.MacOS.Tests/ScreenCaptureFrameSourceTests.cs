using System.Diagnostics;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.MacOS.Capture;

namespace PSRemoteSub.MacOS.Tests;

public class ScreenCaptureFrameSourceTests
{
    /// <summary>
    /// Window id 0 never exists, so this is the same with or without Screen Recording permission: Start returns at once
    /// (ScreenCaptureKit is asynchronous), nothing throws and no frame ever arrives.
    /// </summary>
    [MacFact]
    public async Task ScreenCapture_UnknownWindowId_LatestIsNull_NoThrow()
    {
        using var source = new ScreenCaptureFrameSource();
        var sw = Stopwatch.StartNew();
        source.Start(new WindowInfo(0, 0, new PixelRect(0, 0, 640, 360), false));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Start blocked for {sw.ElapsedMilliseconds} ms");

        for (int i = 0; i < 15; i++)
        {
            Assert.Null(source.Latest);
            await Task.Delay(100);
        }
        Assert.Null(source.Latest);
        Assert.Null(source.LastFrameAt);
        source.Stop();
        source.Stop();
        Assert.Null(source.Latest);
    }

    [MacFact]
    public void Stop_WithoutStart_AndDisposeTwice_NoThrow()
    {
        var source = new ScreenCaptureFrameSource();
        source.Stop();
        Assert.Null(source.Latest);
        source.Dispose();
        source.Dispose();
    }

    [MacFact]
    public void Start_AfterDispose_Throws()
    {
        var source = new ScreenCaptureFrameSource();
        source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => source.Start(new WindowInfo(0, 0, new PixelRect(0, 0, 1, 1), false)));
    }
}
