using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.Core.Tests.Pipeline.Fakes;

public sealed class FakeWindowWatcher : IWindowWatcher
{
    public WindowInfo? Current { get; private set; }
    public event EventHandler<WindowInfo?>? Changed;
    public bool HasSubscribers => Changed is not null;

    public void Start() { }

    /// <summary>Mimics the real watcher: update <see cref="Current"/>, then raise <see cref="Changed"/>.</summary>
    public void Raise(WindowInfo? window)
    {
        Current = window;
        Changed?.Invoke(this, window);
    }

    public void Dispose() { }
}
