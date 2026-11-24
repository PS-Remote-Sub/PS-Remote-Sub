namespace PSRemoteSub.Core.Platform;

public interface IWindowWatcher : IDisposable
{
    WindowInfo? Current { get; }
    event EventHandler<WindowInfo?>? Changed;
    void Start();
}
