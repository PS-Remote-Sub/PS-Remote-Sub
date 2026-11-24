using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Platform;

public interface IFrameSource : IDisposable
{
    void Start(WindowInfo window);
    void Stop();
    /// <summary>Latest captured frame, client area only.</summary>
    Frame? Latest { get; }
    DateTimeOffset? LastFrameAt { get; }
}
