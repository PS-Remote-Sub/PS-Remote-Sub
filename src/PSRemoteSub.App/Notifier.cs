using Avalonia.Threading;
using PSRemoteSub.App.Views;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.App;

/// <summary>"Tray notifications" via a single reused <see cref="ToastWindow"/> (Ruling R10). Callable from any thread.</summary>
internal sealed class Notifier(IOverlayWindowPlatform overlayPlatform)
{
    private ToastWindow? _toast;
    private bool _closed;

    public void Show(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(message));
            return;
        }
        if (_closed)
            return;
        try
        {
            _toast ??= new ToastWindow { OverlayPlatform = overlayPlatform };
            _toast.ShowMessage(message);
        }
        catch (Exception ex)
        {
            Log.Warn("Showing a notification failed", ex); // never worth crashing for
        }
    }

    /// <summary>UI thread. Closes the toast; later calls to <see cref="Show"/> do nothing.</summary>
    public void Close()
    {
        _closed = true;
        _toast?.Close();
        _toast = null;
    }
}
