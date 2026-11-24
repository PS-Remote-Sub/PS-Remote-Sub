using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Capture;

/// <summary>
/// Presents one <see cref="IWindowWatcher"/> and one <see cref="IFrameSource"/> that route to either the Remote Play
/// pair or the capture card pair, depending on <see cref="Selected"/>. The pipeline restarts capture whenever the
/// reported window handle changes, so switching only needs to raise <c>Changed</c>. The inner objects are owned by
/// the caller: <see cref="Dispose"/> disposes neither.
/// </summary>
public sealed class PictureSourceSwitch : IDisposable
{
    private readonly (IWindowWatcher Watcher, IFrameSource Frames) _remotePlay;
    private readonly (IWindowWatcher Watcher, IFrameSource Frames) _captureCard;
    private readonly object _gate = new();
    private readonly SwitchWatcher _watcher;
    private readonly SwitchFrames _frames;
    private PictureSource _selected;
    private bool _disposed;

    public PictureSourceSwitch(
        (IWindowWatcher Watcher, IFrameSource Frames) remotePlay,
        (IWindowWatcher Watcher, IFrameSource Frames) captureCard,
        PictureSource initial)
    {
        _remotePlay = remotePlay;
        _captureCard = captureCard;
        _selected = initial;
        _watcher = new SwitchWatcher(this);
        _frames = new SwitchFrames(this);
        _remotePlay.Watcher.Changed += OnInnerChanged;
        _captureCard.Watcher.Changed += OnInnerChanged;
    }

    public IWindowWatcher Watcher => _watcher;

    public IFrameSource Frames => _frames;

    public PictureSource Selected
    {
        get { lock (_gate) return _selected; }
    }

    public void Select(PictureSource source)
    {
        PictureSource previous;
        lock (_gate)
        {
            if (_disposed || source == _selected)
                return;
            previous = _selected;
            _selected = source;
        }

        PairFor(previous).Frames.Stop();
        var now = PairFor(source).Watcher.Current;
        _watcher.Raise(now);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _remotePlay.Watcher.Changed -= OnInnerChanged;
        _captureCard.Watcher.Changed -= OnInnerChanged;
    }

    private (IWindowWatcher Watcher, IFrameSource Frames) PairFor(PictureSource source) =>
        source == PictureSource.CaptureCard ? _captureCard : _remotePlay;

    private (IWindowWatcher Watcher, IFrameSource Frames) SelectedPair
    {
        get { lock (_gate) return PairFor(_selected); }
    }

    private void OnInnerChanged(object? sender, WindowInfo? info)
    {
        if (ReferenceEquals(sender, SelectedPair.Watcher))
            _watcher.Raise(info);
    }

    private sealed class SwitchWatcher(PictureSourceSwitch owner) : IWindowWatcher
    {
        public WindowInfo? Current => owner.SelectedPair.Watcher.Current;

        public event EventHandler<WindowInfo?>? Changed;

        public void Raise(WindowInfo? info) => Changed?.Invoke(this, info);

        public void Start()
        {
            owner._remotePlay.Watcher.Start();
            owner._captureCard.Watcher.Start();
        }

        public void Dispose()
        {
        }
    }

    private sealed class SwitchFrames(PictureSourceSwitch owner) : IFrameSource
    {
        public Frame? Latest => owner.SelectedPair.Frames.Latest;

        public DateTimeOffset? LastFrameAt => owner.SelectedPair.Frames.LastFrameAt;

        public void Start(WindowInfo window) => owner.SelectedPair.Frames.Start(window);

        public void Stop() => owner.SelectedPair.Frames.Stop();

        public void Dispose()
        {
        }
    }
}
