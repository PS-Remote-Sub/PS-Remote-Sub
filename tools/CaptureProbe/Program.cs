using System.Diagnostics;
using System.Runtime.InteropServices;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Windows.Capture;
using PSRemoteSub.Windows.Imaging;

// Capture spike: find the PS Remote Play window, capture its client area with WGC, save a PNG and report stats.
NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
Console.OutputEncoding = System.Text.Encoding.UTF8;

using var watcher = new Win32WindowWatcher(TimeSpan.FromSeconds(2));
var found = new TaskCompletionSource<WindowInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
watcher.Changed += (_, info) =>
{
    if (info is not null)
        found.TrySetResult(info);
};
watcher.Start();
if (watcher.Current is { } current)
    found.TrySetResult(current);

Console.WriteLine("Waiting up to 10 s for the RemotePlay window...");
if (await Task.WhenAny(found.Task, Task.Delay(TimeSpan.FromSeconds(10))) != found.Task)
{
    Console.Error.WriteLine("FAIL: RemotePlay window not found. Open PS Remote Play and try again.");
    return 1;
}

WindowInfo window = found.Task.Result;
Console.WriteLine($"window=0x{window.Handle:X}, pid={window.ProcessId}, client={window.ClientScreenRect}, minimized={window.IsMinimized}");

using var source = new WgcFrameSource();
source.Start(window);

// WGC delivers the current image once, then a frame only when the window content changes: a still screen (for
// example the console-select screen) gives one frame and ~0 fps, a streaming game gives the stream's rate.
var wait = Stopwatch.StartNew();
while (source.FramesCaptured < 1 && wait.Elapsed < TimeSpan.FromSeconds(10))
    await Task.Delay(20);
if (source.FramesCaptured < 1)
{
    Console.Error.WriteLine("FAIL: no frame in 10 s. Is the window minimized?");
    return 2;
}

long startCount = source.FramesCaptured;
var fpsClock = Stopwatch.StartNew();
await Task.Delay(TimeSpan.FromSeconds(3));
double fps = (source.FramesCaptured - startCount) / fpsClock.Elapsed.TotalSeconds;

// Latest reads back lazily: the first call after a new frame copies it to the CPU, later calls return the same object
// until another frame arrives.
var readClock = Stopwatch.StartNew();
long before = source.FramesCaptured;
Frame frame = source.Latest!;
double readbackMs = readClock.Elapsed.TotalMilliseconds;
Frame again = source.Latest!;
bool noNewFrame = source.FramesCaptured == before;
source.Stop();
Console.WriteLine($"readback={readbackMs:F1} ms, sameReferenceWhenUnchanged={(noNewFrame ? ReferenceEquals(frame, again).ToString() : "n/a (a frame arrived in between)")}");

string pngPath = Path.GetFullPath("probe-client.png");
await new WinRtImageFileWriter().SavePngAsync(frame, pngPath);

Console.WriteLine($"saved {pngPath}");
Console.WriteLine($"size={frame.Width}x{frame.Height}, nonBlackPixels={NonBlackPercent(frame):F1}%, fps≈{fps:F1}");
return 0;

// A pixel counts as non-black when any colour channel exceeds 16 (tolerates near-black letterboxing noise).
static double NonBlackPercent(Frame frame)
{
    long pixels = (long)frame.Width * frame.Height;
    if (pixels == 0)
        return 0;
    long nonBlack = 0;
    byte[] bgra = frame.Bgra;
    for (int i = 0; i < bgra.Length; i += 4)
    {
        if (bgra[i] > 16 || bgra[i + 1] > 16 || bgra[i + 2] > 16)
            nonBlack++;
    }
    return 100.0 * nonBlack / pixels;
}

internal static partial class NativeMethods
{
    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDpiAwarenessContext(nint value);
}
