using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PSRemoteSub.App.Updates;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App;

public partial class App : Application
{
    private static readonly TimeSpan StartFailureNoticeTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan UpdateCheckDelay = TimeSpan.FromSeconds(10);

    private AppHost? _host;
    private TrayController? _tray;
    private AppWindows? _windows;
    private CaptureController? _capture;
    private bool _exiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += OnExit;
            Dispatcher.UIThread.Post(() => _ = StartAsync(desktop));
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Never throws. Any start failure ends in <see cref="ExitAsync"/>, so no headless process keeps the mutex.</summary>
    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var store = new SettingsStore(AppPaths.SettingsFile);
            AppSettings settings;
            var loaded = false;
            try
            {
                settings = store.Load();
                loaded = true;
            }
            catch (Exception ex)
            {
                Log.Warn("Settings file unreadable; running on defaults", ex);
                settings = new AppSettings(); // unreadable file: run on defaults, leave the file alone
            }

            // Always applied on Mac so even the defaults fallback serves on 5055; saved only when the file was read fine,
            // so an unreadable (possibly recoverable) file is never overwritten.
            if (Program.Platform.Kind == PlatformKind.MacOS && MacSettingsMigration.Apply(settings) && loaded)
            {
                try
                {
                    store.Save(settings);
                }
                catch (Exception ex)
                {
                    Log.Warn("Could not save the migrated settings; keeping them in memory", ex);
                }
            }

            _host = new AppHost(settings, store, Program.Platform);
            if (!File.Exists(AppPaths.SettingsFile))
                _host.SaveSettings();

            await _host.StartAsync();
            _windows = new AppWindows(_host);
            // No capture window at all when the card cannot be used on this PC (AppHost runs Remote Play).
            if (_host.CaptureCard is { } card)
            {
                _capture = new CaptureController(_host, _host.CaptureWatcher, card);
                _host.CaptureWindowRequested += OnCaptureWindowRequested;
                _host.CaptureSettingsChanged += OnCaptureSettingsChanged;
                _host.CapturePermissionsGranted += OnCapturePermissionsGranted;
                if (_host.PictureSource == PictureSource.CaptureCard)
                    _capture.Open();
            }
            // Regaining focus is when a permission granted in System Settings shows up (and when a lost one is noticed).
            if (TryGetFeature(typeof(Avalonia.Controls.ApplicationLifetimes.IActivatableLifetime)) is Avalonia.Controls.ApplicationLifetimes.IActivatableLifetime activatable)
            {
                var host = _host;
                activatable.Activated += (_, _) => host.RefreshPermissions();
            }
            _tray = new TrayController(this, _host, () => ExitAsync(desktop));
            // The settings window greets every launch (the user asked for it); closing it leaves the app in the tray.
            _host.RequestSettings();
            _ = CheckForUpdatesLaterAsync(_host, () => ExitAsync(desktop));
            Log.Info("Started");
        }
        catch (Exception ex)
        {
            Log.Error("Start failed", ex);
            try
            {
                _host?.Notify(Loc.Format("StartFailed", ex.Message));
                await Task.Delay(StartFailureNoticeTime);
            }
            finally
            {
                await ExitAsync(desktop);
            }
        }
    }

    /// <summary>
    /// Update check 10 s after startup, then the library update (both no-ops without a repository URL). Runs in the
    /// background on the UI thread's context; never throws.
    /// </summary>
    private static async Task CheckForUpdatesLaterAsync(AppHost host, Func<Task> exitApp)
    {
        try
        {
            await Task.Delay(UpdateCheckDelay);
            await new UpdateChecker(host.Notify, exitApp).CheckAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed", ex); // never disturb the app
        }
        try
        {
            // UI thread: AppHost members are UI-thread only. The download itself runs on the thread pool.
            await Dispatcher.UIThread.InvokeAsync(host.UpdateLibrariesAsync);
        }
        catch (Exception ex)
        {
            Log.Warn("Library update failed", ex); // never disturb the app
        }
    }

    /// <summary>
    /// The picture source changed, or the tray asked for the capture window: open it (or bring it forward) with the card,
    /// close it with Remote Play.
    /// </summary>
    private void OnCaptureWindowRequested(object? sender, EventArgs e)
    {
        if (_host is null || _capture is null)
            return;
        if (_host.PictureSource == PictureSource.CaptureCard)
            _capture.Open();
        else
            _capture.Close();
    }

    private void OnCaptureSettingsChanged(object? sender, EventArgs e) => _capture?.ApplySettings();

    private void OnCapturePermissionsGranted(object? sender, IReadOnlyList<PlatformPermission> granted) =>
        _capture?.RetryAfterPermissionsGranted(granted);

    /// <summary>
    /// UI thread, before the host: the capture window drives the card the host owns (closes it, its audio and the
    /// probe). Never throws.
    /// </summary>
    private void DisposeCapture()
    {
        try
        {
            if (_host is not null)
            {
                _host.CaptureWindowRequested -= OnCaptureWindowRequested;
                _host.CaptureSettingsChanged -= OnCaptureSettingsChanged;
                _host.CapturePermissionsGranted -= OnCapturePermissionsGranted;
            }
            _capture?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Exit: capture window dispose failed", ex); // going away with the process anyway
        }
        _capture = null;
    }

    /// <summary>Never throws; always ends with <c>desktop.Shutdown()</c>, whatever cleanup fails on the way.</summary>
    private async Task ExitAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_exiting)
            return;
        _exiting = true;
        Log.Info("Exit requested");
        try
        {
            try
            {
                _tray?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Exit: tray dispose failed", ex); // the tray is going away with the process anyway
            }
            try
            {
                _windows?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Exit: windows dispose failed", ex); // going away with the process anyway
            }
            DisposeCapture();
            if (_host is not null)
                await _host.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Exit: host dispose failed", ex); // DisposeAsync guards its own steps; exit regardless
        }
        finally
        {
            desktop.Shutdown();
        }
    }

    /// <summary>Shutdown that did not come from the tray (for example Windows sign-out).</summary>
    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_exiting)
            return;
        _exiting = true;
        Log.Info("Shutdown not from the tray (for example Windows sign-out)");
        try
        {
            _tray?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Shutdown step failed", ex); // exiting anyway
        }
        try
        {
            _windows?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Shutdown step failed", ex); // exiting anyway
        }
        DisposeCapture();
        try
        {
            // DisposeAsync does its UI-thread work synchronously, then runs the engine shutdown on the thread pool,
            // so blocking the UI thread here cannot deadlock.
            _host?.DisposeAsync().AsTask().Wait(ExitWait);
        }
        catch (Exception ex)
        {
            Log.Warn("Shutdown step failed", ex); // exiting anyway
        }
    }
}
