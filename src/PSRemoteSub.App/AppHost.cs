using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Threading;
using PSRemoteSub.App.Updates;
using PSRemoteSub.App.Views;
using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Input;
using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Layout;
using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Net;
using PSRemoteSub.Core.Pipeline;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;
using PSRemoteSub.Core.Tv;
using PSRemoteSub.Core.Translation;
using PSRemoteSub.Web;

namespace PSRemoteSub.App;

/// <summary>
/// Composition root: builds the engine (both picture sources and their switch, OCR, AI lane, hub, pipeline), the overlay,
/// hotkeys and the web server, and keeps them in step with <see cref="Settings"/>.
/// </summary>
/// <remarks>
/// Threading: every public member is UI-thread only. Hub, watcher and hotkey events arrive on foreign threads; their
/// handlers here never throw and only <see cref="Dispatcher.Post(Action, DispatcherPriority)"/> the work to the UI
/// thread, where it runs inside a try/catch. <see cref="StateChanged"/> is therefore always raised on the UI thread.
/// </remarks>
public sealed class AppHost : IAsyncDisposable
{
    private const string WebBindAddress = "0.0.0.0";
    private static readonly TimeSpan WatcherPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CaptureWatcherPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AiHttpTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan GoogleHttpTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The library updater's own client: a library zip takes longer than <see cref="AiHttp"/>'s 15 s. Static and never
    /// disposed (one per process).
    /// </summary>
    private static readonly HttpClient LibraryHttp = CreateLibraryHttp();
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(2);

    private readonly SettingsStore _store;
    private readonly IPlatformServices _platform;
    private readonly IOverlayWindowPlatform _overlayPlatform;
    private readonly IKeepAwake _keepAwake;
    private readonly IImageFileWriter _imageWriter;
    private readonly Notifier _notifier;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _webGate = new(1, 1);
    // One ADB operation on the TV at a time: two installs, or two read-modify-writes of the accessibility list, must not overlap.
    private readonly SemaphoreSlim _tvAdbGate = new(1, 1);
    private readonly IOcrEngine _ocr;
    private readonly IOcrLanguageInstaller _ocrInstaller;
    private readonly HashSet<string> _installingOcr = new(StringComparer.Ordinal);
    private readonly LibraryHolder _library = new();

    private SubtitleHub? _hub;
    // The two picture sources, and the switch that presents the selected one to the pipeline, overlay and picker.
    private IWindowWatcher? _remotePlayWatcher;
    private IFrameSource? _remotePlayFrames;
    private ICaptureCard? _captureCard;  // null when the card cannot be used on this PC (see StartAsync)
    private IFrameSource? _captureFrames;
    private PolledWindowWatcher? _captureWatcher;
    private PictureSourceSwitch? _sources;
    private bool _captureWindowOpen;
    private bool _captureWindowMinimized;
    private SubtitlePipeline? _pipeline;
    private AiRefiner? _aiRefiner;
    private ZoneOverlays? _overlay;
    private RegionFramesController? _regionFrames;
    private IGlobalHotkeys? _hotkeys;
    private Task? _runTask;
    private SubtitleWebServer? _web;
    private TvBeacon? _tvBeacon;
    private int? _webFailedPort;
    private bool _webServesPhone;
    private Dictionary<HotkeyAction, string>? _registeredHotkeys;
    private bool _started;
    private bool _paused;
    private bool _keepAwakeActive;
    private bool _disposed;
    // Serialized Settings.Translation as of the last AI rebuild: ApplySettings rebuilds only when it differs.
    private string? _translationSnapshot;
    // The AI error last seen in a status change (the AiAuth toast shows on the change into AiAuth).
    private string? _lastAiError;
    // Profile, languages and library game the library was last (re)loaded for: a change drops it and reloads.
    private string? _libraryKey;
    // Bumped by every library reload request: a build that finishes after a newer request is dropped.
    private int _libraryGeneration;
    // The TV setup screen's calibration pattern is showing (runtime only; see SetTvCalibrating).
    private bool _tvCalibrating;
    // Permissions the current picture source needs, and the ones already asked for in this run (asked once, never in a loop).
    private PermissionNotice? _permNotice;
    private readonly HashSet<PlatformPermission> _permRequested = [];

    public AppHost(AppSettings settings, SettingsStore store, IPlatformServices platform)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(platform);
        Settings = settings;
        _store = store;
        _platform = platform;
        _overlayPlatform = platform.Overlay;
        _keepAwake = platform.KeepAwake;
        _imageWriter = platform.ImageWriter;
        _ocr = platform.Ocr;
        _ocrInstaller = platform.OcrInstaller;
        LanAccess = platform.LanAccess;
        _notifier = new Notifier(_overlayPlatform);

        AiHttp = new HttpClient { Timeout = AiHttpTimeout };
        AiHttp.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PSRemoteSub", Version));
        GoogleHttp = new HttpClient { Timeout = GoogleHttpTimeout };
        GoogleHttp.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PSRemoteSub", Version));
        Secrets = platform.Secrets;
        AiSource = new AiTranslatorSource(() => Settings.Translation, Secrets, AiHttp, GoogleHttp);
        LibraryStore = new LibraryStore(AppPaths.BundledLibraryDir, AppPaths.LibraryDir);
        TvFeed = new TvFeed();
    }

    /// <summary>
    /// UI thread. Raised when status, pause, settings, web server or web client count change, and when an OCR
    /// language install starts or ends.
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// UI thread. Raised for the SelectRegion hotkey, tray item and settings button with the zone to open on (null =
    /// the profile's last zone); <see cref="AppWindows"/> opens the region picker. With no subscriber a notification
    /// says the picker is missing.
    /// </summary>
    public event EventHandler<string?>? RegionSelectionRequested;

    /// <summary>UI thread. Raised to show the main window on a page; <see cref="AppWindows"/> opens or fronts it.</summary>
    public event EventHandler<MainPage>? MainWindowRequested;

    /// <summary>
    /// UI thread. Raised when the picture source changes and by the "เปิดจอ capture card" tray item: the capture window
    /// should open (or come forward) when <see cref="PictureSource"/> is the card, and close when it is Remote Play.
    /// </summary>
    public event EventHandler? CaptureWindowRequested;

    /// <summary>
    /// UI thread. Raised by <see cref="ApplyCaptureSettings"/> after <see cref="AppSettings.Capture"/> was edited: an open
    /// capture window applies the device, mode and audio settings.
    /// </summary>
    public event EventHandler? CaptureSettingsChanged;

    /// <summary>
    /// UI thread. Raised by <see cref="RefreshPermissions"/> when Camera or Microphone goes from missing to granted (the
    /// argument lists the ones granted): an open capture window started before the grant reopens the card or its sound.
    /// Never raised where permissions are never missing (Windows).
    /// </summary>
    public event EventHandler<IReadOnlyList<PlatformPermission>>? CapturePermissionsGranted;

    /// <summary>Live settings. Mutate on the UI thread, then call <see cref="ApplySettings"/> (and <see cref="SaveSettings"/>).</summary>
    public AppSettings Settings { get; }

    public SubtitleHub Hub => _hub ?? throw NotStarted();

    public SubtitlePipeline Pipeline => _pipeline ?? throw NotStarted();

    /// <summary>The translator (Google or AI, by the current mode) for the live translation settings (also used by the Settings Test button).</summary>
    public AiTranslatorSource AiSource { get; }

    /// <summary>Protects API keys at rest (DPAPI, current user).</summary>
    public ISecretProtector Secrets { get; }

    /// <summary>HttpClient for AI providers: 15 s timeout, the app's User-Agent.</summary>
    public HttpClient AiHttp { get; }

    /// <summary>HttpClient for Google Translate: separate from the AI one, 5 s timeout, the app's User-Agent.</summary>
    public HttpClient GoogleHttp { get; }

    /// <summary>The selected picture source's window watcher (see <see cref="PictureSourceSwitch"/>).</summary>
    public IWindowWatcher Watcher => _sources?.Watcher ?? throw NotStarted();

    /// <summary>
    /// Where the picture comes from in this session: <see cref="AppSettings.PictureSource"/>, except Remote Play when the
    /// card is unavailable (the saved choice is kept). Change it with <see cref="SetPictureSource"/>.
    /// </summary>
    public PictureSource PictureSource =>
        Settings.PictureSource == PictureSource.CaptureCard && _started && !IsCaptureAvailable
            ? PictureSource.RemotePlay
            : Settings.PictureSource;

    /// <summary>
    /// Whether the capture card can be used in this session: false when it could not be built (for example Windows N
    /// without the Media Feature Pack, no hardware Direct3D 11, or an OS without one yet). False before start.
    /// </summary>
    public bool IsCaptureAvailable => _captureCard is not null;

    /// <summary>The capture card (owned here; the capture window drives it), or null when unavailable or not started.</summary>
    public ICaptureCard? CaptureCard => _captureCard;

    /// <summary>The capture card's watcher: its probe (set by the capture window) reports where the video is.</summary>
    public PolledWindowWatcher CaptureWatcher => _captureWatcher ?? throw NotStarted();

    /// <summary>Whether the capture window is open (see <see cref="SetCaptureWindowOpen"/>), for the status line and settings.</summary>
    public bool CaptureWindowOpen => _captureWindowOpen;

    /// <summary>What the TV should draw now, per zone: fed by the overlay controllers, streamed to the TV app.</summary>
    public TvFeed TvFeed { get; }

    /// <summary>The translation libraries: shipped next to the exe and downloaded to <c>%AppData%</c>.</summary>
    public LibraryStore LibraryStore { get; }

    /// <summary>The library in use: null when the active profile has none, it is not English → Thai, or it is loading.</summary>
    public ActiveLibrary? ActiveLibrary => _library.Current;

    /// <summary>UI thread. Bumped after each library install, so a list of libraries knows to refresh.</summary>
    public int LibraryListVersion { get; private set; }

    /// <summary>Latest full client-area frame from capture, or null (for the region picker).</summary>
    public Frame? LatestFrame => _sources?.Frames.Latest;

    /// <summary>When capture last delivered a frame, or null.</summary>
    public DateTimeOffset? LastFrameAt => _sources?.Frames.LastFrameAt;

    /// <summary>
    /// The selected source's target (client rect in physical pixels): the Remote Play window, or the capture window's video
    /// while the card plays. Null when it is not found.
    /// </summary>
    public WindowInfo? TargetWindow => _sources?.Watcher.Current;

    /// <summary>Current pipeline status (initial before start).</summary>
    public PipelineStatus Status => _hub?.Status ?? PipelineStatus.Initial;

    /// <summary>Number of browsers connected to the web page.</summary>
    public int WebClients => _hub?.WebClients ?? 0;

    /// <summary>Firewall rule check and setup for the web port. <c>IsPortOpen</c> blocks: call it via <c>Task.Run</c>.</summary>
    public ILanAccessSetup? LanAccess { get; }

    /// <summary>The OS services this host runs on.</summary>
    public IPlatformServices Platform => _platform;

    /// <summary>Phone URL (first LAN address) while the web server runs, otherwise null.</summary>
    public string? WebUrl { get; private set; }

    /// <summary>
    /// Whether the phone page is being served. The server can also run for the TV alone (<see cref="TvSettings.Enabled"/>);
    /// that does not count here.
    /// </summary>
    public bool IsWebRunning => _web is not null && _webServesPhone;

    /// <summary>Reading is paused (runtime only; not saved).</summary>
    public bool IsPaused => _paused;

    /// <summary>The current status line in Thai (tray tooltip).</summary>
    public string StatusText =>
        StatusCopy.Describe(_hub?.Status ?? PipelineStatus.Initial, _paused, DateTimeOffset.UtcNow, PictureSource,
            _captureWindowOpen ? _captureCard?.State : null, SelectRegionHotkeyLabel());

    /// <summary>The region hotkey as shown on this OS (<c>⌃⌥⇧S</c> on Mac), or the setting's text when it does not parse.</summary>
    private string SelectRegionHotkeyLabel() =>
        HotkeyLabels.DisplaySetting(Settings.Hotkeys.GetValueOrDefault(HotkeyAction.SelectRegion, ""), _platform.Kind);

    /// <summary>The <c>User-Agent</c> product token: <c>PSRemoteSub/&lt;informational version&gt;</c>.</summary>
    public static string Version { get; } =
        typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppHost).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>UI thread. Builds and starts everything. Call once.</summary>
    public async Task StartAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            throw new InvalidOperationException("AppHost already started.");
        _started = true;

        _hub = new SubtitleHub(TimeProvider.System);
        _remotePlayWatcher = _platform.CreateRemotePlayWatcher(WatcherPollInterval);
        _remotePlayFrames = _platform.CreateRemotePlayFrames();
        _captureCard = _platform.TryCreateCaptureCard();
        _captureFrames = _captureCard?.Frames ?? new NoFrames();
        // Finds nothing until the capture window sets its probe (see CaptureController); never, without a card.
        _captureWatcher = new PolledWindowWatcher(static () => null, CaptureWatcherPollInterval);
        _sources = new PictureSourceSwitch((_remotePlayWatcher, _remotePlayFrames), (_captureWatcher, _captureFrames),
            PictureSource); // Remote Play for this session when the card is unavailable
        if (_captureCard is null && Settings.PictureSource == PictureSource.CaptureCard)
            Notify(Loc.Get("CaptureUnavailable"));
        var watcher = _sources.Watcher;

        // The refiner reports AI status through the pipeline that is built from it: late-bound delegate (see the
        // SubtitlePipeline constructor). It only reports once lines flow or Refresh runs, both after the assignment.
        SubtitlePipeline? built = null;
        _aiRefiner = new AiRefiner(_hub, AiSource, TimeProvider.System, change => built?.UpdateAiStatus(change));
        built = new SubtitlePipeline(watcher, _sources.Frames, _ocr, _hub,
            TimeProvider.System, PipelineOptions.Default, _aiRefiner, OnSpeakerLearned, _library);
        _pipeline = built;
        PushConfig(); // also starts loading the profile's library
        ApplyTvSettings(); // before the overlay: the TV config is set when the first view arrives, and a new Tv.PcId is saved

        // While "Send to TV" is off no zone text reaches the feed, so /tv/events carries only the config (enabled=false);
        // SetTvEnabledAsync refreshes the overlays to fill or empty it at once.
        _overlay = new ZoneOverlays(_overlayPlatform, Settings.GetActive, () => PictureSource,
            (id, v) => TvFeed.SetZone(id, Settings.Tv.Enabled ? v : null), SelectRegionHotkeyLabel);
        _overlay.Sync();
        _overlay.SetEnabled(Settings.OverlayEnabled);
        ApplyAiLane();
        _regionFrames = new RegionFramesController(new RegionFramesWindow { OverlayPlatform = _overlayPlatform }, Settings.GetActive);
        _regionFrames.SetEnabled(Settings.ShowRegionFrames);

        // Subscribe before anything can publish.
        _hub.Changed += OnHubChanged;
        watcher.Changed += OnWatcherChanged;
        if (_captureCard is not null)
            _captureCard.StateChanged += OnCaptureStateChanged;
        RebuildAi(); // after subscribing: a config error (for example AiKeyNeeded) is a status change
        watcher.Start(); // starts both sources' watchers
        _overlay.SetTarget(watcher.Current);
        _regionFrames.SetTarget(watcher.Current);

        var pipeline = _pipeline;
        var ct = _cts.Token;
        _runTask = Task.Run(() => pipeline.RunAsync(ct), CancellationToken.None);
        UpdateKeepAwake();

        _hotkeys = _platform.CreateHotkeys();
        _hotkeys.Pressed += OnHotkeyPressed;
        RegisterHotkeys();

        await SyncWebAsync(retryFailed: true);
        // Started once; the info lambda is re-evaluated every tick, so toggling Tv.Enabled or the server needs no restart.
        _tvBeacon = TvBeacon.Start(
            () => Settings.Tv.Enabled && _web is { } w ? new TvBeaconInfo(Settings.Tv.PcId, Environment.MachineName, w.Port) : null,
            LanAddresses.GetBroadcastTargets,
            onPass: OnTvBeaconPass);
        _ = UpdateTvAppAsync(); // background: never delays startup
        RefreshPermissions();
        RaiseStateChanged();
    }

    /// <summary>Permissions the selected picture source needs: Screen Recording for Remote Play, Camera and Microphone for the card.</summary>
    private PlatformPermission[] NeededPermissions() => PictureSource == PictureSource.CaptureCard
        ? [PlatformPermission.Camera, PlatformPermission.Microphone]
        : [PlatformPermission.ScreenRecording];

    /// <summary>UI thread. The needed permissions that are missing (always empty where the OS has none, as on Windows).</summary>
    public IReadOnlyList<PlatformPermission> MissingPermissions => _permNotice?.Missing ?? [];

    /// <summary>Thai explanation of what a permission is for.</summary>
    public static string PermissionText(PlatformPermission p) => Loc.Get("Perm" + p);

    /// <summary>
    /// UI thread. Re-checks the needed permissions (on start, when the app regains focus, when the source changes). Tells
    /// the user once per permission that goes missing, and asks the OS once per run for one that was never asked
    /// (Camera, Microphone; Screen Recording, whose request is what lists the app in System Settings).
    /// </summary>
    public void RefreshPermissions()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;
        try
        {
            var perms = _platform.Permissions;
            _permNotice ??= new PermissionNotice(perms);
            var missingBefore = _permNotice.Missing;
            var needed = NeededPermissions();
            bool changed = _permNotice.Refresh(needed);
            var granted = PermissionNotice.NewlyGranted(missingBefore, _permNotice.Missing, needed);
            foreach (var p in _permNotice.Missing.ToArray())
            {
                if (!_permNotice.ShouldNotify(p))
                    continue;
                Notify(PermissionText(p));
                Log.Info($"Permission missing: {p} ({perms.Check(p)})");
                bool askable = perms.Check(p) == PermissionState.NotDetermined || p == PlatformPermission.ScreenRecording;
                if (askable && _permRequested.Add(p))
                    _ = RequestPermissionAsync(perms, p);
            }
            if (changed)
                RaiseStateChanged();
            if (granted.Any(p => p is PlatformPermission.Camera or PlatformPermission.Microphone))
            {
                Log.Info($"Permission granted: {string.Join(", ", granted)}");
                CapturePermissionsGranted?.Invoke(this, granted);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Permission check failed", ex);
        }
    }

    private async Task RequestPermissionAsync(IPermissions perms, PlatformPermission p)
    {
        try
        {
            await perms.RequestAsync(p);
        }
        catch (Exception ex)
        {
            Log.Warn($"Permission request failed for {p}", ex);
        }
        // The answer may arrive on any thread; the check is back on the UI thread.
        Dispatcher.UIThread.Post(RefreshPermissions);
    }

    /// <summary>UI thread. Opens the OS settings page where the permission can be granted.</summary>
    public void OpenPermissionSettings(PlatformPermission p)
    {
        Dispatcher.UIThread.VerifyAccess();
        _platform.Permissions.OpenSettings(p);
    }

    /// <summary>UI thread. Pushes <see cref="Settings"/> to the pipeline, overlay, hotkeys, keep-awake and web server.</summary>
    public void ApplySettings()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_started || _disposed)
            return;
        PushConfig();
        if (TranslationSnapshot() != _translationSnapshot)
            RebuildAi();
        PushTvConfig();
        _overlay?.Sync(); // zones may have been added or removed, or the profile switched
        _overlay?.SetEnabled(Settings.OverlayEnabled);
        _regionFrames?.SetEnabled(Settings.ShowRegionFrames); // also redraws: the boxes or the profile may have changed
        RegisterHotkeys();
        UpdateKeepAwake();
        _ = SyncWebAsync(retryFailed: false);
        RaiseStateChanged();
    }

    /// <summary>
    /// UI thread. After <see cref="AppSettings.Translation"/> was edited: rebuilds the AI translator (always, so a
    /// retyped identical key still clears an auth pause), pushes the context-line count, saves and raises
    /// <see cref="StateChanged"/>.
    /// </summary>
    public void ApplyTranslationSettings()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;
        RebuildAi();
        if (_started)
            PushConfig();
        SaveSettings();
        RaiseStateChanged();
    }

    /// <summary>UI thread. Sets <see cref="AppSettings.WebEnabled"/> and starts or stops the server to match.</summary>
    public async Task SetWebEnabledAsync(bool on)
    {
        Dispatcher.UIThread.VerifyAccess();
        Settings.WebEnabled = on;
        if (!_started || _disposed)
            return;
        await SyncWebAsync(retryFailed: true);
    }

    /// <summary>
    /// UI thread. Sets <see cref="TvSettings.Enabled"/>, pushes it to the TV, fills or empties the feed's zones (off sends
    /// no text) and starts or stops the server to match (the server runs while the phone page or the TV wants it).
    /// The caller saves.
    /// </summary>
    public async Task SetTvEnabledAsync(bool on)
    {
        Dispatcher.UIThread.VerifyAccess();
        bool changed = Settings.Tv.Enabled != on;
        Settings.Tv.Enabled = on;
        PushTvConfig(); // the config first: a connected TV learns enabled=false before its zones are cleared
        if (changed)
            _overlay?.Refresh(); // re-runs the TV sink for every zone with the new gate
        if (!_started || _disposed)
            return;
        await SyncWebAsync(retryFailed: true);
    }

    /// <summary>
    /// UI thread. After <see cref="AppSettings.Tv"/> was edited (and once at start): pushes it to <see cref="TvFeed"/> and
    /// saves, so a <c>Tv.PcId</c> created for an old settings file persists.
    /// </summary>
    public void ApplyTvSettings()
    {
        Dispatcher.UIThread.VerifyAccess();
        PushTvConfig();
        SaveSettings();
    }

    /// <summary>
    /// UI thread. Pushes <see cref="AppSettings.Tv"/> and the calibration flag to <see cref="TvFeed"/> without saving: for
    /// live slider changes, which save on their own (debounced) schedule.
    /// </summary>
    public void PushTvConfig() =>
        TvFeed.SetConfig(Settings.Tv.ToConfig(Environment.MachineName, _tvCalibrating));

    /// <summary>
    /// Thread pool. The installer for the bundled adb and the newest TV app: the latest <c>PS-Remote-Sub-TV</c> GitHub
    /// release when it is newer than the APK bundled in the <c>tv</c> folder (downloaded once into
    /// <see cref="AppPaths.TvApkDir"/>), else the bundled one. Builds without a repository URL (local builds) never
    /// contact GitHub. Null when adb is missing or no APK is available. <paramref name="downloading"/> runs before the
    /// GitHub check.
    /// </summary>
    private static async Task<TvInstaller?> CreateTvInstallerAsync(PlatformKind kind, Action? downloading, CancellationToken ct)
    {
        string baseDir = AppContext.BaseDirectory;
        string adb = TvBundle.AdbPath(baseDir, kind);
        if (!File.Exists(adb))
            return null;

        string bundledPath = TvBundle.ApkPath(baseDir);
        TvApk? apk = File.Exists(bundledPath) && TvBundle.BundledVersion(baseDir) is { } bundledVersion
            ? new TvApk(bundledPath, bundledVersion)
            : null;
        if (TvApkDownloader.RepositoryFor(UpdateChecker.ReadRepositoryUrl()) is { } repo)
        {
            downloading?.Invoke();
            var downloader = new TvApkDownloader(LibraryHttp, repo, AppPaths.TvApkDir);
            apk = await downloader.GetNewerAsync(apk?.Version ?? 0, ct).ConfigureAwait(false) ?? apk;
        }
        if (apk is null)
            return null;
        Log.Info($"TV app: using versionCode {apk.Version} ({(apk.Path == bundledPath ? "bundled" : "downloaded")})");
        return new TvInstaller(new AdbClient(adb, new ProcessRunner()), apk.Path, apk.Version);
    }

    // Set by the beacon's thread and by a failed install; read by the TV tab. macOS only.
    private volatile bool _tvLocalNetworkBlocked;

    /// <summary>
    /// Whether the TV looks unreachable because macOS denies Local Network access (the beacon's LAN sends or the last
    /// install's adb connect failed with host/network unreachable). Always false off macOS. Cleared by a later success.
    /// </summary>
    public bool TvLocalNetworkBlocked => _tvLocalNetworkBlocked;

    /// <summary>Beacon thread: a pass with a blocked-looking failure raises the TV tab hint; a clean pass clears it.</summary>
    private void OnTvBeaconPass(BeaconPassResult pass)
    {
        bool next = LocalNetworkHint.AfterBeaconPass(_platform.Kind, pass, LanAddresses.GetLocalNets);
        if (next == _tvLocalNetworkBlocked)
            return;
        _tvLocalNetworkBlocked = next;
        try
        {
            Dispatcher.UIThread.Post(RaiseStateChanged);
        }
        catch (Exception ex)
        {
            Log.Warn($"TV beacon hint update failed: {ex.GetType().Name}"); // the dispatcher is shutting down
        }
    }

    /// <summary>
    /// UI thread. Whether ADB work on the TV is running: <see cref="InstallTvAppAsync"/>, <see cref="CollectTvDiagnosticsAsync"/> or the startup update. Changes
    /// raise <see cref="StateChanged"/>.
    /// </summary>
    public bool IsInstallingTvApp { get; private set; }

    /// <summary>
    /// UI thread. Runs <paramref name="work"/> on the thread pool behind <see cref="_tvAdbGate"/>, with
    /// <see cref="IsInstallingTvApp"/> set (and <see cref="StateChanged"/> raised) for its whole length.
    /// </summary>
    private async Task<T> RunTvAdbAsync<T>(Func<CancellationToken, Task<T>> work)
    {
        IsInstallingTvApp = true;
        RaiseStateChanged();
        var ct = _cts.Token;
        bool entered = false;
        try
        {
            await _tvAdbGate.WaitAsync(ct);
            entered = true;
            return await Task.Run(() => work(ct), CancellationToken.None);
        }
        finally
        {
            if (entered)
                _tvAdbGate.Release();
            IsInstallingTvApp = false;
            if (!_disposed)
                RaiseStateChanged();
        }
    }

    /// <summary>
    /// UI thread. Installs the newest TV app (see <see cref="CreateTvInstallerAsync"/>) over ADB (finding the TV when <paramref name="endpoint"/> is null) and
    /// enables its service. On success saves <see cref="TvSettings.AdbAddress"/> and turns the TV on (starting the server).
    /// The ADB work runs on the thread pool; <paramref name="progress"/> reports each step. Raises
    /// <see cref="StateChanged"/> when it starts and ends. Never throws: a failure is a result with a Tv* error key
    /// (<c>TvBundleMissing</c> when adb is missing, or the APK could not be downloaded and none is bundled).
    /// </summary>
    public async Task<TvInstallResult> InstallTvAppAsync(string? endpoint, IProgress<TvInstallStep> progress)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(progress);
        if (IsInstallingTvApp || _disposed)
            return new TvInstallResult(false, endpoint, "TvInstallFailed");
        var kind = _platform.Kind;
        try
        {
            var result = await RunTvAdbAsync(async ct =>
            {
                if (await CreateTvInstallerAsync(kind, () => progress.Report(TvInstallStep.Downloading), ct) is not { } installer)
                {
                    Log.Warn("TV app install: adb or the TV app is missing (no download and no bundled APK)");
                    return new TvInstallResult(false, endpoint, "TvBundleMissing");
                }
                return await installer.InstallAsync(endpoint, progress, ct);
            });
            _tvLocalNetworkBlocked = LocalNetworkHint.AfterInstall(_platform.Kind, _tvLocalNetworkBlocked, result.Ok, result.LocalNetworkBlocked);
            if (!_disposed)
                RaiseStateChanged(); // RunTvAdbAsync raised it before the flag was updated
            Log.Info(result.Ok ? "TV app installed" : $"TV app install failed: {result.ErrorKey}");
            if (result.Ok && !_disposed)
            {
                Settings.Tv.AdbAddress = result.Endpoint;
                await SetTvEnabledAsync(true);
                SaveSettings();
            }
            return result;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return new TvInstallResult(false, endpoint, "TvInstallFailed"); // the app is closing: nothing to report
        }
        catch (Exception ex)
        {
            Log.Warn("TV app install failed", ex);
            return new TvInstallResult(false, endpoint, "TvInstallFailed");
        }
    }

    /// <summary>
    /// UI thread. Collects the TV diagnostics zip (see <see cref="TvDiagnostics"/>) over ADB, finding the TV when
    /// <paramref name="endpoint"/> is null, and writes it to the Desktop (else Downloads, else the user folder). Runs
    /// behind the same gate as the install and the startup update, with <see cref="IsInstallingTvApp"/> set. Never
    /// downloads an APK. Never throws: a failure is a result with a Tv* error key (<c>TvDiagBundleMissing</c> when adb
    /// is missing). Logs the outcome and the zip's file name only.
    /// </summary>
    public async Task<TvDiagResult> CollectTvDiagnosticsAsync(string? endpoint, IProgress<TvDiagStep> progress)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(progress);
        if (IsInstallingTvApp || _disposed)
            return new TvDiagResult(false, null, "TvDiagWriteFailed");
        var kind = _platform.Kind;
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string adbPath = TvBundle.AdbPath(baseDir, kind);
            if (!File.Exists(adbPath))
            {
                Log.Warn("TV diagnostics: adb is missing");
                return new TvDiagResult(false, null, "TvDiagBundleMissing");
            }
            int? bundled = TvBundle.BundledVersion(baseDir);
            int? cached = TvApkDownloader.NewestCachedVersion(AppPaths.TvApkDir);
            int? newest = bundled is null ? cached : cached is null ? bundled : Math.Max(bundled.Value, cached.Value);
            var tv = Settings.Tv;
            var info = new TvDiagPcInfo(
                Version,
                RuntimeInformation.OSDescription,
                newest,
                _web?.Port,
                $"Enabled={tv.Enabled}\nMode={tv.Mode}\nAdbAddress={tv.AdbAddress}\n",
                TvDiagText.PcIdHash(tv.PcId), // hashing boundary: the raw PcId never leaves AppHost
                TvFeed.Clients(),
                [AppPaths.LogFile, Path.Combine(Path.GetDirectoryName(AppPaths.LogFile) ?? "", "log.1.txt")]);
            string outDir = DiagnosticsOutputDir();
            var diag = new TvDiagnostics(new AdbClient(adbPath, new ProcessRunner()), info);
            var result = await RunTvAdbAsync(ct => diag.CollectAsync(endpoint, outDir, progress, ct));
            Log.Info(result.Ok
                ? $"TV diagnostics written: {Path.GetFileName(result.ZipPath)}"
                : $"TV diagnostics failed: {result.ErrorKey}");
            return result;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return new TvDiagResult(false, null, "TvDiagWriteFailed"); // the app is closing: nothing to report
        }
        catch (Exception ex)
        {
            Log.Warn("TV diagnostics failed", ex);
            return new TvDiagResult(false, null, "TvDiagWriteFailed");
        }
    }

    /// <summary>The Desktop, else Downloads, else the user folder: the first that exists (and is not empty).</summary>
    private static string DiagnosticsOutputDir()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        foreach (var dir in new[] { desktop, profile.Length > 0 ? Path.Combine(profile, "Downloads") : "" })
            if (dir.Length > 0 && Directory.Exists(dir))
                return dir;
        return profile;
    }

    /// <summary>
    /// UI thread, once at start. Updates the TV app over ADB when it is older than the newest one (latest GitHub
    /// release, else bundled; see <see cref="CreateTvInstallerAsync"/>), in the background.
    /// Only when the TV is enabled and was set up from this PC (<see cref="TvSettings.AdbAddress"/>). Failures are
    /// logged; a TV that no longer trusts this PC, or an app signed with another key, gets one notification. Never throws.
    /// </summary>
    private async Task UpdateTvAppAsync()
    {
        try
        {
            var tv = Settings.Tv;
            if (!tv.Enabled || tv.AdbAddress is not { } endpoint || !AdbClient.IsValidEndpoint(endpoint)
                || IsInstallingTvApp || _disposed)
                return;
            var kind = _platform.Kind;
            var result = await RunTvAdbAsync(async ct =>
            {
                if (await CreateTvInstallerAsync(kind, null, ct) is not { } installer)
                {
                    Log.Warn("TV app update skipped: adb or the TV app is missing");
                    return new TvInstallResult(true, endpoint, null);
                }
                return await installer.UpdateIfOlderAsync(endpoint, ct);
            });
            if (result.Ok || _disposed)
                return;
            Log.Warn($"TV app update failed: {result.ErrorKey}");
            if (result.ErrorKey == "TvNeedsApproval")
                Notify(Loc.Get("TvUpdateNeedsApproval"));
            else if (result.ErrorKey == "TvSignatureMismatch")
                Notify(Loc.Get("TvUpdateSignatureMismatch"));
        }
        catch (OperationCanceledException)
        {
            // The app is closing.
        }
        catch (Exception ex)
        {
            Log.Warn("TV app update failed", ex);
        }
    }

    /// <summary>UI thread. Turns the TV's calibration pattern on or off (not saved).</summary>
    public void SetTvCalibrating(bool calibrating)
    {
        Dispatcher.UIThread.VerifyAccess();
        _tvCalibrating = calibrating;
        PushTvConfig();
    }

    /// <summary>UI thread. Writes <see cref="Settings"/> to disk; a failure is shown as a notification.</summary>
    public void SaveSettings()
    {
        try
        {
            _store.Save(Settings);
        }
        catch (Exception ex)
        {
            Log.Warn("Settings save failed", ex);
            Notify(Loc.Format("SettingsSaveFailed", ex.Message));
        }
    }

    /// <summary>UI thread. Pauses or resumes reading.</summary>
    public void SetPaused(bool paused)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_paused == paused)
            return;
        _paused = paused;
        _overlay?.SetPaused(paused);
        if (_started && !_disposed)
            PushConfig();
        UpdateKeepAwake();
        RaiseStateChanged();
    }

    /// <summary>UI thread. Raised by <see cref="SelectProfile"/> just before the active profile changes (pages commit pending edits).</summary>
    public event EventHandler? ActiveProfileChanging;

    /// <summary>UI thread. Switches the active profile, applies and saves.</summary>
    public void SelectProfile(string name)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (Settings.Profiles.All(p => p.Name != name) || Settings.ActiveProfile == name)
            return;
        ActiveProfileChanging?.Invoke(this, EventArgs.Empty);
        Settings.ActiveProfile = name;
        ApplySettings();
        SaveSettings();
    }

    /// <summary>
    /// UI thread. Adds a new game profile named <paramref name="name"/> and makes it active (raising
    /// <see cref="ActiveProfileChanging"/> first), applies and saves. Returns false (nothing changed) for a blank name or
    /// one already taken (case-insensitive).
    /// </summary>
    public bool AddProfile(string name)
    {
        Dispatcher.UIThread.VerifyAccess();
        name = (name ?? "").Trim();
        if (name.Length == 0 || Settings.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            return false;
        ActiveProfileChanging?.Invoke(this, EventArgs.Empty);
        Settings.Profiles.Add(GameProfile.Create(name));
        Settings.ActiveProfile = name;
        ApplySettings();
        SaveSettings();
        return true;
    }

    /// <summary>
    /// UI thread. Renames the active profile (it stays active; <see cref="ActiveProfileChanging"/> is raised first so
    /// open pages commit into it), applies and saves. Returns false for a blank name or one taken by another profile.
    /// </summary>
    public bool RenameActiveProfile(string name)
    {
        Dispatcher.UIThread.VerifyAccess();
        name = (name ?? "").Trim();
        var active = Settings.GetActive();
        if (name.Length == 0 || Settings.Profiles.Any(p => p != active && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (name == active.Name)
            return true;
        ActiveProfileChanging?.Invoke(this, EventArgs.Empty);
        active.Name = name;
        Settings.ActiveProfile = name; // the active profile keeps being the renamed one
        ApplySettings();
        SaveSettings();
        return true;
    }

    /// <summary>
    /// UI thread. Deletes the active profile and activates the first remaining one (raising
    /// <see cref="ActiveProfileChanging"/> first), applies and saves. Returns false when it is the only profile.
    /// </summary>
    public bool DeleteActiveProfile()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (Settings.Profiles.Count <= 1)
            return false;
        ActiveProfileChanging?.Invoke(this, EventArgs.Empty);
        Settings.Profiles.Remove(Settings.GetActive());
        Settings.ActiveProfile = Settings.Profiles[0].Name;
        ApplySettings();
        SaveSettings();
        return true;
    }

    /// <summary>UI thread. Shows or hides the overlay, applies and saves.</summary>
    public void SetOverlayEnabled(bool enabled)
    {
        Dispatcher.UIThread.VerifyAccess();
        Settings.OverlayEnabled = enabled;
        ApplySettings();
        SaveSettings();
    }

    /// <summary>UI thread. Shows or hides the debug outlines of the OCR and caption boxes, applies and saves.</summary>
    public void SetRegionFramesEnabled(bool enabled)
    {
        Dispatcher.UIThread.VerifyAccess();
        Settings.ShowRegionFrames = enabled;
        ApplySettings();
        SaveSettings();
    }

    /// <summary>
    /// UI thread. Raises <see cref="RegionSelectionRequested"/> for <paramref name="zoneId"/> (null = the active
    /// profile's last zone), or says the picker is missing.
    /// </summary>
    public void RequestRegionSelection(string? zoneId = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (RegionSelectionRequested is { } handler)
            handler(this, zoneId);
        else
            Notify(Loc.Get("RegionPickerMissing"));
    }

    /// <summary>UI thread. Raises <see cref="MainWindowRequested"/> for the given page.</summary>
    public void RequestMainWindow(MainPage page = MainPage.Home)
    {
        Dispatcher.UIThread.VerifyAccess();
        MainWindowRequested?.Invoke(this, page);
    }

    /// <summary>UI thread. The "ตั้งค่า" tray item: the main window's Home page.</summary>
    public void RequestSettings() => RequestMainWindow(MainPage.Home);

    /// <summary>
    /// UI thread. Switches the picture source: the pipeline, overlay and region picker follow the new source's target,
    /// <see cref="CaptureWindowRequested"/> opens or closes the capture window, and a notification asks to check the
    /// boxes. Applies and saves. Does nothing when unchanged.
    /// </summary>
    public void SetPictureSource(PictureSource source)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || source == PictureSource)
            return;
        if (source == PictureSource.CaptureCard && _started && !IsCaptureAvailable)
        {
            Notify(Loc.Get("CaptureUnavailable"));
            return;
        }
        Settings.PictureSource = source;
        Log.Info($"Picture source: {source}");
        _sources?.Select(source);
        RaiseCaptureWindowRequested();
        Notify(Loc.Format("SourceSwitched", SelectRegionHotkeyLabel()));
        ApplySettings();
        SaveSettings();
        RefreshPermissions();
    }

    /// <summary>UI thread. Raises <see cref="CaptureWindowRequested"/> (the tray item).</summary>
    public void RequestCaptureWindow()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_disposed)
            RaiseCaptureWindowRequested();
    }

    /// <summary>
    /// UI thread. After <see cref="AppSettings.Capture"/> was edited (device or mode): saves and raises
    /// <see cref="CaptureSettingsChanged"/>, so an open capture window reopens the card with them.
    /// </summary>
    public void ApplyCaptureSettings()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;
        SaveSettings();
        try
        {
            CaptureSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn("Applying the capture settings failed", ex);
        }
    }

    /// <summary>
    /// UI thread. Told by the capture window when it opens and closes: the status line shows the card's state while it
    /// is open and "closed" otherwise. Raises <see cref="StateChanged"/> on a change.
    /// </summary>
    public void SetCaptureWindowOpen(bool open)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_captureWindowOpen == open)
            return;
        _captureWindowOpen = open;
        if (!open)
            _captureWindowMinimized = false;
        if (_disposed)
            return;
        UpdateKeepAwake();
        RaiseStateChanged();
    }

    /// <summary>UI thread. Told by the capture window when it is minimised or restored (for keep-awake).</summary>
    public void SetCaptureWindowMinimized(bool minimized)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_captureWindowMinimized == minimized)
            return;
        _captureWindowMinimized = minimized;
        if (!_disposed)
            UpdateKeepAwake();
    }

    /// <summary>UI thread. Hides the overlay while true (for example while the region picker shows the frozen frame).</summary>
    public void SetOverlaySuppressed(bool suppressed)
    {
        Dispatcher.UIThread.VerifyAccess();
        _overlay?.SetSuppressed(suppressed);
        _regionFrames?.SetSuppressed(suppressed);
    }

    /// <summary>Any thread. Whether the OCR language is installed; runs the (WinRT) check on the thread pool.</summary>
    public Task<bool> IsOcrLanguageAvailableAsync(SourceLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return Task.Run(() => _ocr.IsAvailable(language.OcrTag));
    }

    /// <summary>UI thread. Whether <see cref="InstallOcrLanguageAsync"/> is running for this language.</summary>
    public bool IsInstallingOcrLanguage(SourceLanguage language) => _installingOcr.Contains(language.Code);

    /// <summary>
    /// UI thread. Installs an OCR language (UAC prompt), then forgets the engine cache so the pipeline and
    /// <see cref="IsOcrLanguageAvailableAsync"/> see it. Returns whether the installer reported success; never throws.
    /// Raises <see cref="StateChanged"/> when the install starts and ends.
    /// </summary>
    public async Task<bool> InstallOcrLanguageAsync(SourceLanguage language)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(language);
        if (!_installingOcr.Add(language.Code))
            return false;
        RaiseStateChanged();
        try
        {
            // InstallAsync starts an elevated process synchronously, which blocks until UAC is answered: keep that
            // off the UI thread. The await resumes on the UI thread.
            var installer = _ocrInstaller;
            var ct = _cts.Token;
            bool ok = await Task.Run(() => installer.InstallAsync(language, ct), CancellationToken.None);
            if (ok)
                _ocr.Invalidate(language.OcrTag);
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn($"OCR language install ({language.OcrTag}) failed", ex);
            return false;
        }
        finally
        {
            _installingOcr.Remove(language.Code);
            if (!_disposed)
                RaiseStateChanged();
        }
    }

    /// <summary>Any thread. Shows a toast at the bottom-right of the primary screen for 4 s.</summary>
    public void Notify(string message) => _notifier.Show(message);

    /// <summary>UI thread. Whether the active profile's languages let a library apply (English → Thai only).</summary>
    public bool LibraryLanguagesApply =>
        LibraryHolder.AppliesTo(Settings.GetActive().SourceLang, TargetCode());

    /// <summary>A library folder name that cannot leave the library root once trimmed (settings may be edited by hand).</summary>
    internal static bool IsLibraryName(string? game) => LibraryStore.IsGameName(game);

    /// <summary>
    /// UI thread. (Re)loads the active profile's library off the UI thread, or drops it when the profile has none, the
    /// languages are not English → Thai or the library is missing. A newer request wins over a build still running.
    /// Never throws.
    /// </summary>
    public void ReloadLibrary()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;
        int generation = ++_libraryGeneration;
        if (WantedLibraryGame() is not { } game)
        {
            SetLibrary(null);
            return;
        }
        _ = LoadLibraryAsync(generation, game);
    }

    /// <summary>
    /// UI thread. Downloads newer library releases (nothing without a repository URL). A new version of the active
    /// profile's library is loaded at once. Only libraries a profile uses get a toast: a player who uses none sees
    /// nothing. Never throws.
    /// </summary>
    public async Task UpdateLibrariesAsync()
    {
        try
        {
            Dispatcher.UIThread.VerifyAccess();
            if (_disposed)
                return;
            var updater = new LibraryUpdater(LibraryHttp, LibraryStore, UpdateChecker.ReadRepositoryUrl());
            var ct = _cts.Token;
            var installed = await Task.Run(() => updater.CheckAndInstallAsync(ct), CancellationToken.None);
            if (_disposed || installed.Count == 0)
                return;
            LibraryListVersion++;
            var profileGames = Settings.Profiles.Select(p => p.LibraryGame).ToList();
            var used = installed.Where(l => LibraryStore.IsInUse(profileGames, l.Game)).ToList();
            if (used.Count > 0)
                Notify(Loc.Format("LibraryUpdated", string.Join(", ", used.Select(l => l.Version))));
            if (WantedLibraryGame() is { } game
                && installed.Any(l => string.Equals(l.Game, game, StringComparison.OrdinalIgnoreCase)))
                ReloadLibrary();
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            Log.Warn("Library update failed", ex); // never disturb the app
        }
    }

    /// <summary>
    /// UI thread. Saves the last OCR crop (raw and preprocessed) to <c>captures\yyyyMMdd-HHmmss-raw.png</c> /
    /// <c>-pre.png</c> and reports the paths.
    /// </summary>
    public async Task SaveFrameAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_pipeline?.LastCapture is not { } capture)
        {
            Notify(Loc.Get("NoFrameToSave"));
            return;
        }
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var rawPath = Path.Combine(AppPaths.CapturesDir, $"{stamp}-raw.png");
        var prePath = Path.Combine(AppPaths.CapturesDir, $"{stamp}-pre.png");
        try
        {
            await _imageWriter.SavePngAsync(capture.Raw, rawPath);
            await _imageWriter.SavePngAsync(capture.Processed, prePath);
            Notify(Loc.Format("FrameSaved", rawPath + Environment.NewLine + prePath));
        }
        catch (Exception ex)
        {
            Log.Warn("Saving the frame failed", ex);
            Notify(Loc.Format("FrameSaveFailed", ex.Message));
        }
    }

    /// <summary>
    /// Stops everything; never throws. Call on the UI thread: the UI-bound parts (keep-awake, windows) run
    /// synchronously before the first await, and the rest runs on the thread pool and never needs the UI thread,
    /// so a caller may block on the returned task. Each step is guarded so one failure cannot skip the others.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (Dispatcher.UIThread.CheckAccess())
        {
            BestEffort(() => _keepAwake.SetActive(false));
            BestEffort(() => _overlay?.Close());
            BestEffort(() => _regionFrames?.Close());
            BestEffort(_notifier.Close);
        }
        BestEffort(() =>
        {
            if (_hub is not null)
                _hub.Changed -= OnHubChanged;
        });
        BestEffort(() =>
        {
            if (_sources is not null)
                _sources.Watcher.Changed -= OnWatcherChanged;
        });
        BestEffort(() =>
        {
            if (_captureCard is not null)
                _captureCard.StateChanged -= OnCaptureStateChanged;
        });
        BestEffort(() =>
        {
            if (_hotkeys is null)
                return;
            _hotkeys.Pressed -= OnHotkeyPressed;
            _hotkeys.Dispose();
        });

        // Off the UI context, so a continuation (for example inside Kestrel's shutdown) can never need a UI thread
        // that the caller is blocking.
        try
        {
            await Task.Run(ShutdownEngineAsync).ConfigureAwait(false);
            Log.Info("Engine stopped");
        }
        catch (Exception ex)
        {
            Log.Warn("Engine shutdown failed", ex);
            // ShutdownEngineAsync guards every step; nothing more to do on the way out.
        }
    }

    private async Task ShutdownEngineAsync()
    {
        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Shutdown: cancellation callback failed", ex); // still stop the rest
        }
        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(ShutdownWait).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelled as intended.
            }
            catch (Exception ex)
            {
                Log.Warn("Shutdown: pipeline did not stop cleanly", ex); // for example a tick stuck in OCR: carry on
            }
        }
        BestEffort(() => _pipeline?.Dispose());
        BestEffort(() => _aiRefiner?.Dispose()); // not owned by the pipeline; cancels an AI request in flight
        BestEffort(() =>
        {
            if (_library.Current is { } library)
                LogLibraryCounts(library, "closed");
        });
        // The switch, then both sources it routes to; the card last (its frame source reads from it). The capture
        // window (which drives the card) was closed by the App before this.
        BestEffort(() => _sources?.Dispose());
        BestEffort(() => _remotePlayWatcher?.Dispose());
        BestEffort(() => _remotePlayFrames?.Dispose());
        BestEffort(() => _captureWatcher?.Dispose());
        BestEffort(() => _captureFrames?.Dispose());
        BestEffort(() => _captureCard?.Dispose());

        if (_tvBeacon is { } beacon)
        {
            _tvBeacon = null;
            try
            {
                await beacon.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("Shutdown: TV beacon stop failed", ex);
            }
        }

        bool gate = false;
        try
        {
            gate = await _webGate.WaitAsync(ShutdownWait).ConfigureAwait(false);
            if (_web is { } web)
            {
                _web = null;
                await web.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Shutdown: web server stop failed", ex); // best effort on the way out
        }
        finally
        {
            if (gate)
                _webGate.Release();
        }
        BestEffort(AiHttp.Dispose);
        BestEffort(GoogleHttp.Dispose);
    }

    private static void BestEffort(Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Log.Warn("Shutdown step failed", ex); // shutdown continues with the next step
        }
    }

    // ---- foreign-thread event handlers: post to the UI thread, never throw -------------------------------------

    private void OnHubChanged(object? sender, HubEvent e) => PostToUi(() => HandleHubEvent(e));

    private void OnWatcherChanged(object? sender, WindowInfo? window) => PostToUi(() =>
    {
        _overlay?.SetTarget(window);
        _regionFrames?.SetTarget(window);
    });

    private void OnHotkeyPressed(object? sender, HotkeyAction action) => PostToUi(() => HandleHotkey(action));

    // The status line shows the card's state while the capture window is open and nothing is found; keep-awake follows
    // whether the card plays.
    private void OnCaptureStateChanged(object? sender, CaptureState state) => PostToUi(() =>
    {
        UpdateKeepAwake();
        RaiseStateChanged();
    });

    private void RaiseCaptureWindowRequested()
    {
        try
        {
            CaptureWindowRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn("Opening or closing the capture window failed", ex);
        }
    }

    private void PostToUi(Action action)
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed)
                    return;
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Warn("UI update failed", ex); // the next event redraws from current state
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Posting to the UI thread failed", ex); // the dispatcher is shutting down
        }
    }

    // ---- UI thread ----------------------------------------------------------------------------------------------

    private void HandleHubEvent(HubEvent e)
    {
        switch (e)
        {
            case SubtitlePublished published:
                _overlay?.Show(published.Entry);
                break;
            case SubtitleRefined refined:
                _overlay?.Refine(refined.Entry);
                break;
            case SubtitleCleared cleared:
                _overlay?.Clear(cleared.ZoneId);
                break;
            case ZoneHidden hidden:
                _overlay?.HideZone(hidden.ZoneId);
                break;
            case TriggerChanged trigger:
                _regionFrames?.SetTriggerActive(trigger.ZoneId, trigger.Active);
                break;
            case StatusChanged status:
                _overlay?.SetStatus(status.Status);
                NotifyAiAuth(status.Status.AiError);
                UpdateKeepAwake();
                RaiseStateChanged();
                break;
            case ClientsChanged:
                RaiseStateChanged();
                break;
        }
    }

    private void HandleHotkey(HotkeyAction action)
    {
        Log.Info($"Hotkey: {action}");
        switch (action)
        {
            case HotkeyAction.SelectRegion:
                RequestRegionSelection(null);
                break;
            case HotkeyAction.ToggleOverlay:
                SetOverlayEnabled(!Settings.OverlayEnabled);
                break;
            case HotkeyAction.TogglePause:
                SetPaused(!_paused);
                Notify(Loc.Get(_paused ? "Paused" : "Resumed"));
                break;
            case HotkeyAction.SaveFrame:
                _ = SaveFrameAsync();
                break;
            case HotkeyAction.ToggleRegionFrames:
                SetRegionFramesEnabled(!Settings.ShowRegionFrames);
                break;
        }
    }

    /// <summary>
    /// Pipeline thread: a speaker name got its Thai spelling or colour. Stored in that profile (by name, so a profile
    /// switch meanwhile cannot misfile it) and saved on the UI thread. Never throws.
    /// </summary>
    private void OnSpeakerLearned(string profileName, string sourceName, SpeakerInfo info)
    {
        var copy = info.Copy();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var profile = Settings.Profiles.FirstOrDefault(p => p.Name == profileName);
                if (profile is null)
                    return;
                // Merge, never replace: two reports can arrive out of order, and an older one must not erase a field.
                if (profile.Speakers.Comparer != StringComparer.OrdinalIgnoreCase)
                    profile.Speakers = AppSettings.MergeSpeakers(profile.Speakers);
                if (profile.Speakers.TryGetValue(sourceName, out var known))
                {
                    known.Thai = copy.Thai ?? known.Thai;
                    known.Color = copy.Color ?? known.Color;
                }
                else
                {
                    profile.Speakers[sourceName] = copy;
                }
                SaveSettings();
            }
            catch (Exception ex)
            {
                Log.Warn($"Storing a speaker name failed: {ex.GetType().Name}");
            }
        });
    }

    private PipelineConfig BuildConfig()
    {
        var profile = Settings.GetActive();
        var source = SourceLanguages.All.FirstOrDefault(l => l.Code == profile.SourceLang) ?? SourceLanguages.All[0];
        var target = TargetCode();
        // The profile's own title first, then the library's.
        var title = string.IsNullOrWhiteSpace(profile.GameTitle) ? _library.Current?.Data.Info.Title : profile.GameTitle;
        var gameTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        return new PipelineConfig([.. profile.Zones.Select(ZoneConfig.From)], source, target, _paused, gameTitle,
            Math.Clamp(Settings.Translation.ContextLines, 0, 10), profile.Name,
            AppSettings.MergeSpeakers(profile.Speakers), // a copy; never throws on names that differ only in case
            profile.SpeakerColors);
    }

    private string TargetCode() => string.IsNullOrWhiteSpace(Settings.TargetLang) ? "th" : Settings.TargetLang;

    /// <summary>
    /// Pushes the config to the pipeline. When the profile, its languages or its library changed, the library is
    /// dropped first (so no line of the new config can read the old one) and reloaded after.
    /// </summary>
    private void PushConfig()
    {
        var profile = Settings.GetActive();
        string key = string.Join('\n', profile.Name, profile.SourceLang, TargetCode(), profile.LibraryGame ?? "");
        bool changed = key != _libraryKey;
        if (changed)
        {
            _libraryKey = key;
            _libraryGeneration++; // a build still running is for the old key
            if (_library.Current is { } old)
            {
                LogLibraryCounts(old, "dropped");
                _library.Set(null);
            }
        }
        Pipeline.UpdateConfig(BuildConfig());
        if (changed)
            ReloadLibrary();
    }

    /// <summary>The active profile's library game when a library can apply to it (en → th), otherwise null.</summary>
    private string? WantedLibraryGame()
    {
        var profile = Settings.GetActive();
        return IsLibraryName(profile.LibraryGame) && LibraryLanguagesApply ? profile.LibraryGame!.Trim() : null;
    }

    /// <summary>Builds the library on the thread pool; sets it when no newer request came meanwhile. Never throws.</summary>
    private async Task LoadLibraryAsync(int generation, string game)
    {
        try
        {
            var store = LibraryStore;
            var watch = Stopwatch.StartNew();
            var library = await Task.Run(() => BuildLibrary(store, game), CancellationToken.None);
            watch.Stop();
            if (_disposed || generation != _libraryGeneration)
                return; // a newer request owns the holder now
            SetLibrary(library);
            if (library is not null)
                Log.Info($"Library {game} {library.Data.Version}: {library.Data.Lines.Count} lines, "
                    + $"built in {watch.ElapsedMilliseconds} ms");
            else
                Log.Info($"Library {game}: not found");
        }
        catch (Exception ex)
        {
            Log.Warn($"Library {game}: load failed", ex);
        }
    }

    /// <summary>
    /// Thread pool. Null when the library is missing or unreadable. The index comes from its cache in the user folder
    /// when that matches (see <see cref="LibraryStore.Build"/>).
    /// </summary>
    private static ActiveLibrary? BuildLibrary(LibraryStore store, string game) => store.Build(game);

    private static HttpClient CreateLibraryHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PSRemoteSub");
        return http;
    }

    /// <summary>UI thread. Replaces the library, logs the old one's counts and pushes the (game title) config.</summary>
    private void SetLibrary(ActiveLibrary? library)
    {
        var old = _library.Current;
        if (ReferenceEquals(old, library))
            return;
        if (old is not null)
            LogLibraryCounts(old, library is null ? "dropped" : "replaced");
        _library.Set(library);
        if (_started && !_disposed)
            Pipeline.UpdateConfig(BuildConfig()); // the game title may come from the library
        RaiseStateChanged();
    }

    /// <summary>Counts only: never subtitle text.</summary>
    private static void LogLibraryCounts(ActiveLibrary library, string what)
    {
        var (hits, misses) = library.Counts;
        Log.Info($"Library {library.Data.Game} {library.Data.Version} {what}: {hits} hits, {misses} misses, "
            + $"{library.Matcher.LearnedCount} learned this session");
    }

    /// <summary>
    /// Rebuilds the AI translator from <see cref="AppSettings.Translation"/> and applies it to the AI lane at once, so a
    /// settings edit clears a stale AI status without waiting for the next line. No network calls.
    /// </summary>
    private void RebuildAi()
    {
        _translationSnapshot = TranslationSnapshot();
        AiSource.Rebuild();
        _aiRefiner?.Refresh();
        ApplyAiLane();
    }

    /// <summary>
    /// The translation lane's queue and parallel requests from the settings. In AI mode, with requests sent at once,
    /// their answers can be due together: the overlay keeps as many waiting for their turn, so none is dropped before it
    /// is shown. In Google mode the lane is one request at a time (ParallelRequests is an AI-only setting): extra
    /// in-flight jobs would only queue on the Google semaphore and bypass the per-zone MaxWaiting drop rule.
    /// </summary>
    private void ApplyAiLane()
    {
        var t = Settings.Translation;
        int parallel = t.Mode == TranslationMode.Google ? AiRefiner.MinMaxParallel : t.ParallelRequests;
        if (_aiRefiner is not null)
        {
            _aiRefiner.MaxWaiting = t.MaxWaitingLines;
            _aiRefiner.MaxParallel = parallel;
        }
        _overlay?.SetMaxWaiting(parallel > 1
            ? Math.Max(OverlayLines.DefaultMaxWaiting, parallel + t.MaxWaitingLines)
            : OverlayLines.DefaultMaxWaiting);
    }

    private string TranslationSnapshot() => JsonSerializer.Serialize(Settings.Translation);

    /// <summary>
    /// Shows the AiAuth toast whenever the AI error becomes AiAuth. A new translator generation always clears the AI
    /// error first, so this is once per rejected-key episode.
    /// </summary>
    private void NotifyAiAuth(string? aiError)
    {
        // Both stop AI until the user acts, so both deserve one notice per episode.
        bool becameStop = aiError is AiRefiner.AiAuthKey or AiRefiner.AiBillingKey && _lastAiError != aiError;
        _lastAiError = aiError;
        if (becameStop)
            Notify(Loc.Get(aiError!));
    }

    /// <summary>
    /// Keeps the PC awake only while subtitles are actually being read: the pipeline runs, is not paused, has a
    /// visible target (the Remote Play window, or the capture window's video: <see cref="PipelineStatus.RemotePlayFound"/>),
    /// a region and an installed OCR language. Also while someone plays through the capture window (open, not minimised,
    /// the card playing), whatever the reading state: the controller is on the console, so Windows sees no input.
    /// Re-evaluated on every status change and capture window or card change.
    /// UI thread only: SetThreadExecutionState is per thread.
    /// </summary>
    private void UpdateKeepAwake()
    {
        if (_disposed)
            return;
        var s = Status;
        bool reading = _runTask is not null && !_paused && !s.Paused && s.RemotePlayFound && !s.NoRegion
            && !s.Minimized && !s.OcrUnavailable;
        bool watching = PictureSource == PictureSource.CaptureCard
            && CaptureWindowRules.KeepsDisplayAwake(_captureWindowOpen, _captureWindowMinimized, _captureCard?.State);
        bool active = Settings.KeepAwake && (reading || watching);
        if (active != _keepAwakeActive)
            Log.Info(active ? "Keep-awake on" : "Keep-awake off");
        _keepAwakeActive = active;
        _keepAwake.SetActive(active);
    }

    /// <summary>(Re)registers hotkeys when the configured set differs from what is registered.</summary>
    private void RegisterHotkeys()
    {
        if (_hotkeys is null)
            return;
        var wanted = new Dictionary<HotkeyAction, string>(Settings.Hotkeys);
        if (_registeredHotkeys is not null && _registeredHotkeys.Count == wanted.Count
            && _registeredHotkeys.All(kv => wanted.TryGetValue(kv.Key, out var v) && v == kv.Value))
            return;

        _hotkeys.UnregisterAll();
        var failed = new List<string>();
        foreach (var (action, text) in wanted.OrderBy(kv => kv.Key))
        {
            HotkeySpec spec;
            try
            {
                spec = HotkeySpec.Parse(text);
            }
            catch (FormatException)
            {
                failed.Add(text);
                continue;
            }
            if (!_hotkeys.Register(action, spec))
                failed.Add(spec.ToString());
        }
        _registeredHotkeys = wanted;
        if (failed.Count > 0)
        {
            Log.Warn($"Hotkey registration failed: {string.Join(", ", failed)}");
            Notify(Loc.Format("HotkeyInUse", string.Join(", ", failed)));
        }
    }

    /// <summary>Starts, stops or restarts the web server to match the settings. Never throws.</summary>
    /// <param name="retryFailed">Try again on a port that failed before (explicit user action).</param>
    private async Task SyncWebAsync(bool retryFailed)
    {
        try
        {
            await _webGate.WaitAsync();
            try
            {
                int port = Settings.WebPort;
                bool want = (Settings.WebEnabled || Settings.Tv.Enabled) && !_disposed;
                if (_web is { } running && (!want || running.Port != port || _webServesPhone != Settings.WebEnabled))
                {
                    _web = null;
                    WebUrl = null;
                    await running.DisposeAsync();
                }
                if (want && _web is null && (retryFailed || _webFailedPort != port))
                {
                    try
                    {
                        var web = await SubtitleWebServer.StartAsync(Hub, port, WebBindAddress, TvFeed, Settings.WebEnabled);
                        if (_disposed)
                        {
                            await web.DisposeAsync();
                            return;
                        }
                        _web = web;
                        _webServesPhone = Settings.WebEnabled;
                        _webFailedPort = null;
                        WebUrl = BuildWebUrl(web.Port);
                        Log.Info($"Web server listening on port {web.Port}");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Web server start on port {port} failed", ex);
                        _webFailedPort = port;
                        Notify(Loc.Format("WebStartFailed", port));
                    }
                }
                if (!want)
                    _webFailedPort = null;
            }
            finally
            {
                _webGate.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Web server stop failed", ex); // state is already cleared
        }
        if (!_disposed)
            RaiseStateChanged();
    }

    private static string BuildWebUrl(int port)
    {
        string host = "localhost";
        try
        {
            if (LanAddresses.GetCandidates() is [var first, ..])
                host = first.ToString();
        }
        catch (Exception)
        {
            // No network information: localhost still works on this PC.
        }
        return $"http://{host}:{port}/";
    }

    private void RaiseStateChanged()
    {
        if (StateChanged is not { } handlers)
            return;
        // One throwing subscriber must not starve the others.
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Warn("State change subscriber failed", ex); // subscribers redraw on the next change
            }
        }
    }

    private static InvalidOperationException NotStarted() => new("AppHost has not been started.");

    /// <summary>The capture card's frame source when the card is unavailable: never has a frame.</summary>
    private sealed class NoFrames : IFrameSource
    {
        public Frame? Latest => null;

        public DateTimeOffset? LastFrameAt => null;

        public void Start(WindowInfo window)
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }
}
