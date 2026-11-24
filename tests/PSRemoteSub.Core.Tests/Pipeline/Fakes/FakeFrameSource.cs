using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.Core.Tests.Pipeline.Fakes;

/// <summary>
/// Records Start/Stop calls as "Start:{handle}" / "Stop". Frames come only from <see cref="Push"/>; Start does not
/// clear them, so a test can deliver a frame before the tick that starts capture.
/// </summary>
public sealed class FakeFrameSource : IFrameSource
{
    public List<string> Calls { get; } = [];
    public Exception? ThrowOnStart { get; set; }
    public Frame? Latest { get; private set; }
    public DateTimeOffset? LastFrameAt { get; private set; }

    public void Start(WindowInfo window)
    {
        Calls.Add($"Start:{window.Handle}");
        if (ThrowOnStart is { } ex)
            throw ex;
    }

    public void Stop() => Calls.Add("Stop");

    public void Push(Frame frame, DateTimeOffset at)
    {
        Latest = frame;
        LastFrameAt = at;
    }

    public void Dispose() { }
}
