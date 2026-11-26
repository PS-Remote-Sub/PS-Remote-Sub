using Avalonia;
using Avalonia.Threading;
using PSRemoteSub.App.Views;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Notices;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.App;

/// <summary>
/// "Tray notifications" (Ruling R10): one <see cref="ToastWindow"/> per shown <see cref="Notice"/>, stacked at the
/// bottom-right of the primary screen's working area, newest on top, at most <see cref="NoticeQueue.MaxShown"/>
/// (<see cref="NoticeQueue"/> decides what is shown). <see cref="Show"/> and <see cref="DismissTopic"/> are callable
/// from any thread.
/// </summary>
/// <param name="onAction">UI thread. A notice's button was clicked (the notice is already gone): its action id.</param>
/// <param name="onClosed">UI thread. The user closed a sticky notice with ×.</param>
internal sealed class Notifier(IOverlayWindowPlatform overlayPlatform, Action<string> onAction, Action<Notice> onClosed)
{
    /// <summary>The update notice's topic (its icon is the download arrow).</summary>
    public const string UpdateTopic = "update";

    private const double ScreenMargin = 12;
    private const double Gap = 12;

    private IReadOnlyList<Notice> _shown = [];
    private readonly Dictionary<Notice, ToastWindow> _windows = new(ReferenceEqualityComparer.Instance);
    private bool _closed;

    public void Show(Notice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        OnUiThread(() =>
        {
            _shown = NoticeQueue.Add(_shown, notice);
            Sync();
        });
    }

    /// <summary>Takes the notices about <paramref name="topic"/> away (the problem was fixed).</summary>
    public void DismissTopic(string topic) => OnUiThread(() =>
    {
        if (!_shown.Any(n => n.Topic == topic))
            return;
        _shown = NoticeQueue.DismissTopic(_shown, topic);
        Sync();
    });

    /// <summary>UI thread. Closes every toast; later calls do nothing.</summary>
    public void Close()
    {
        _closed = true;
        _shown = [];
        foreach (var window in _windows.Values.ToList())
            CloseWindow(window);
        _windows.Clear();
    }

    private void OnUiThread(Action action)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnUiThread(action));
            return;
        }
        if (_closed)
            return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn("Showing a notification failed", ex); // never worth crashing for
        }
    }

    private void Dismiss(Notice notice)
    {
        _shown = NoticeQueue.Dismiss(_shown, notice);
        Sync();
    }

    /// <summary>Closes the windows of notices no longer shown, creates the new ones, and restacks them.</summary>
    private void Sync()
    {
        foreach (var (notice, window) in _windows.ToList())
        {
            if (_shown.Contains(notice, ReferenceEqualityComparer.Instance))
                continue;
            _windows.Remove(notice);
            CloseWindow(window);
        }

        foreach (var notice in _shown)
        {
            if (!_windows.ContainsKey(notice))
                _windows[notice] = Create(notice);
        }
        // Any toast window can answer: they all share the screens.
        if (_windows.Values.FirstOrDefault()?.Screens is not { } screens
            || (screens.Primary ?? screens.All.FirstOrDefault()) is not { } screen)
            return;
        double scaling = screen.Scaling;
        var area = screen.WorkingArea;
        int margin = (int)Math.Round(ScreenMargin * scaling);
        int gap = (int)Math.Round(Gap * scaling);
        int width = (int)Math.Ceiling(ToastWindow.CardWidth * scaling);
        int bottom = area.Bottom - margin;

        // Oldest at the bottom, newest on top: a new notice never moves the ones already on screen.
        for (int i = _shown.Count - 1; i >= 0; i--)
        {
            var window = _windows[_shown[i]];
            double height = window.IsVisible ? window.Bounds.Height : window.MeasureHeight();
            int top = bottom - (int)Math.Ceiling(height * scaling);
            window.Position = new PixelPoint(area.Right - width - margin, top);
            if (!window.IsVisible)
                window.Show(); // ShowActivated is false: no focus change
            bottom = top - gap;
        }
    }

    private ToastWindow Create(Notice notice)
    {
        var window = new ToastWindow { OverlayPlatform = overlayPlatform };
        window.Present(notice);
        window.ActionClicked += (_, id) =>
        {
            Dismiss(notice);
            Safe(() => onAction(id));
        };
        window.CloseClicked += (_, _) =>
        {
            Dismiss(notice);
            Safe(() => onClosed(notice));
        };
        window.Expired += (_, _) => Dismiss(notice);
        // Text wraps to the final height once shown: restack.
        window.SizeChanged += (_, e) =>
        {
            if (e.HeightChanged && !_closed && _windows.ContainsKey(notice))
                Safe(Sync);
        };
        return window;
    }

    private static void CloseWindow(ToastWindow window) => Safe(window.Close);

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn("Notification action failed", ex);
        }
    }
}
