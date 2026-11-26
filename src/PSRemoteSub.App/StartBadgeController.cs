using Avalonia.Threading;
using PSRemoteSub.App.Views;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Layout;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.App;

/// <summary>
/// Shows the start badge for 4 s (then a fade) when translation starts, per <see cref="StartBadgeGate"/>; hides it at once
/// when reading stops (pause, overlay off, window lost or minimised, picker open, exit). UI thread only.
/// </summary>
internal sealed class StartBadgeController(StartBadgeWindow window, Func<IReadOnlyList<string>> keyCaps)
{
    private static readonly TimeSpan Visible = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(400);

    private readonly StartBadgeGate _gate = new();
    private readonly DispatcherTimer _timer = new();
    private WindowInfo? _target;
    private bool _suppressed, _closed, _fading;

    /// <summary>The state translation depends on; call on every change (cheap).</summary>
    public void Update(WindowInfo? target, bool paused, bool overlayEnabled)
    {
        if (_closed)
            return;
        bool found = target is { IsMinimized: false } t && t.ClientScreenRect.Width > 0 && t.ClientScreenRect.Height > 0;
        _target = found ? target : null;
        bool start = _gate.Update(found, paused, overlayEnabled);
        if (!_gate.Active)
        {
            Hide();
            return;
        }
        if (start)
            Start();
        else if (_timer.IsEnabled && !_fading)
            Place(); // the game window moved while the badge is up
    }

    /// <summary>Hides the badge while true (the region picker is open).</summary>
    public void SetSuppressed(bool suppressed)
    {
        _suppressed = suppressed;
        if (suppressed)
            Hide();
    }

    public void Close()
    {
        _closed = true;
        Hide();
        window.Close();
    }

    private void Start()
    {
        if (_suppressed)
            return;
        _fading = false;
        Place();
        Arm(Visible, OnVisibleEnd);
    }

    private void Place()
    {
        if (_target is null)
            return;
        try
        {
            window.ShowBadge(_target.ClientScreenRect, keyCaps());
        }
        catch (Exception ex)
        {
            Log.Warn("Start badge failed", ex); // cosmetic: translation goes on
            Hide();
        }
    }

    private void OnVisibleEnd()
    {
        _fading = true;
        window.FadeOut();
        Arm(Fade, Hide);
    }

    private void Arm(TimeSpan after, Action then)
    {
        _timer.Stop();
        // A new handler each time: the one armed before must not fire for a restarted badge.
        _timer.Tick -= _tick;
        _tick = (_, _) =>
        {
            _timer.Stop();
            then();
        };
        _timer.Tick += _tick;
        _timer.Interval = after;
        _timer.Start();
    }

    private EventHandler? _tick;

    private void Hide()
    {
        _timer.Stop();
        _fading = false;
        window.HideBadge();
    }
}
