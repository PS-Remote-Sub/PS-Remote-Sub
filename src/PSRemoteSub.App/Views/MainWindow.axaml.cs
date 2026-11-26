using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using PSRemoteSub.App.Updates;
using PSRemoteSub.App.Views.Pages;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App.Views;

/// <summary>
/// The app's main window: sidebar (game picker, Home, settings menu, pause, help) and a page host. Closing hides it to
/// the tray; <see cref="CloseForExit"/> really closes it.
/// </summary>
public partial class MainWindow : Window
{
    private const string SunIcon = "M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM12 2.5v2M12 19.5v2M4.6 4.6l1.4 1.4M18 18l1.4 1.4M2.5 12h2M19.5 12h2M4.6 19.4 6 18M18 6l1.4-1.4";
    private const string MoonIcon = "M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z";
    private const string PauseGlyph = "M9 5v14M15 5v14";
    private const string PlayGlyph = "M8 5l11 7-11 7z";

    private readonly AppHost? _host;
    private PlatformPermission[]? _shownMissingPermissions; // what the permission banner shows (null: not built yet)
    private Func<Control>? _homeFactory;
    private Control? _home;
    private readonly Dictionary<MainPage, SettingsPage> _pages = [];
    private SettingsPage? _current;
    private MainPage _currentPage = MainPage.Home;
    private string? _shownGame;
    private bool _exiting;
    private bool _syncingGames;

    /// <summary>For the XAML loader and designer only.</summary>
    public MainWindow() => InitializeComponent();

    internal MainWindow(AppHost host) : this()
    {
        _host = host;
        foreach (var nav in NavButtons())
            nav.IsCheckedChanged += OnNavChanged;
        GamePicker.SelectionChanged += (_, _) => Safe(OnGamePicked);
        PauseButton.Click += (_, _) => Safe(() => host.SetPaused(!host.IsPaused));
        HelpButton.Click += (_, _) => Safe(OpenHelp);
        ThemeButton.Click += (_, _) => Safe(ToggleTheme);
        host.StateChanged += OnHostStateChanged;
        host.ActiveProfileChanging += OnActiveProfileChanging;
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += OnThemeChanged;
        Closing += OnClosing;
        Activated += (_, _) => Safe(host.RefreshPermissions); // the user may have just granted one in System Settings
        Closed += (_, _) =>
        {
            CommitAndFlushCurrent();
            host.StateChanged -= OnHostStateChanged;
            host.ActiveProfileChanging -= OnActiveProfileChanging;
            if (Application.Current is { } a)
                a.ActualThemeVariantChanged -= OnThemeChanged;
        };
        host.RefreshPermissions();
        Refresh();
        UpdateThemeButton();
        ShowPage(MainPage.Home);
    }

    private AppHost Host => _host ?? throw new InvalidOperationException("No host");

    /// <summary>Sets what the Home page shows (Task 5's page); until set, Home is empty.</summary>
    internal Func<Control>? HomeFactory
    {
        get => _homeFactory;
        set
        {
            _homeFactory = value;
            _home = null;
            if (NavHome.IsChecked == true)
                ShowPage(MainPage.Home);
        }
    }

    /// <summary>
    /// Builds the settings page for a <see cref="MainPage"/>. One instance per page is kept for the window's life.
    /// </summary>
    internal Func<MainPage, SettingsPage?>? PageFactory { get; set; }

    /// <summary>
    /// Shows a page in the host and keeps its nav item checked. A page with no <see cref="SettingsPage"/> shows Home. Safe to call from a nav button's
    /// IsCheckedChanged.
    /// </summary>
    public void ShowPage(MainPage page)
    {
        if (page == MainPage.Home)
        {
            LeaveCurrentPage();
            _currentPage = MainPage.Home;
            NavHome.IsChecked = true;
            _home ??= _homeFactory?.Invoke();
            PageHost.Content = _home;
            return;
        }
        if (_current is not null && _currentPage == page)
        {
            CheckNav(page);
            return;
        }
        var target = GetSettingsPage(page);
        if (target is null)
        {
            ShowPage(MainPage.Home);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => NavHome.IsChecked = true);
            return;
        }
        LeaveCurrentPage();
        _current = target;
        _currentPage = page;
        PageHost.Content = target;
        CheckNav(page);
        _shownGame = Host.Settings.ActiveProfile;
        target.OnShown();
    }

    private SettingsPage? GetSettingsPage(MainPage page)
    {
        if (_pages.TryGetValue(page, out var cached))
            return cached;
        var created = PageFactory?.Invoke(page);
        if (created is not null)
            _pages[page] = created;
        return created;
    }

    private void CheckNav(MainPage page)
    {
        var nav = NavButtons().FirstOrDefault(n => n.Tag is MainPage p && p == page);
        if (nav is null)
            return;
        nav.IsChecked = true;
        // Re-asserted after the nav group's own update (see ShowPage) so a call from IsCheckedChanged keeps it lit.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_currentPage == page)
                nav.IsChecked = true;
        });
    }

    /// <summary>Commits half-typed fields of the shown settings page and writes pending changes.</summary>
    private void LeaveCurrentPage()
    {
        if (_current is not { } current)
            return;
        Safe(() =>
        {
            current.CommitPending();
            current.FlushSave();
        });
        _current = null;
    }

    private void CommitAndFlushCurrent()
    {
        if (_current is not { } current)
            return;
        Safe(() =>
        {
            current.CommitPending();
            current.FlushSave();
        });
    }

    /// <summary>Closes for real (app exit); a normal close only hides.</summary>
    internal void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    private IEnumerable<RadioButton> NavButtons() =>
        this.GetLogicalDescendants().OfType<RadioButton>().Where(r => r.Classes.Contains("nav"));

    private void OnNavChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true, Tag: MainPage page })
            Safe(() => ShowPage(page));
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Only the user's own close (X, Cmd+W) hides; sign-out, shutdown and app quit must not be blocked.
        if (_exiting || e.CloseReason != WindowCloseReason.WindowClosing || e.IsProgrammatic)
            return;
        e.Cancel = true; // X hides to the tray; translation keeps running
        CommitAndFlushCurrent();
        Hide();
    }

    private void OnHostStateChanged(object? sender, EventArgs e) => Safe(() =>
    {
        Refresh();
        ShowPermissionBanner();
        ReloadIfGameChanged();
    });

    // Any game switch (sidebar, tray, legacy window): the shown page commits into the game being left first.
    private void OnActiveProfileChanging(object? sender, EventArgs e) => CommitAndFlushCurrent();

    private void ReloadIfGameChanged()
    {
        if (_current is not { } page || _shownGame == Host.Settings.ActiveProfile)
            return;
        _shownGame = Host.Settings.ActiveProfile;
        Safe(page.OnShown);
    }

    /// <summary>Reloads the shown settings page (the window was shown again).</summary>
    internal void ReloadCurrentPage()
    {
        if (_current is not { } page)
            return;
        _shownGame = Host.Settings.ActiveProfile;
        Safe(page.OnShown);
    }

    /// <summary>
    /// One row per missing permission (what it is for, and a button to its System Settings page); hidden when none. Rebuilt
    /// only when the missing set changed: StateChanged fires often, and a rebuild under the pointer can swallow a click.
    /// </summary>
    private void ShowPermissionBanner()
    {
        var missing = Host.MissingPermissions;
        if (_shownMissingPermissions is { } shown && shown.SequenceEqual(missing))
            return;
        _shownMissingPermissions = [.. missing];
        PermBanner.Children.Clear();
        PermBanner.IsVisible = missing.Count > 0;
        foreach (var p in missing)
        {
            var open = new Button { Content = Loc.Get("PermOpenSettings"), Classes = { "btn", "sm" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var permission = p;
            open.Click += (_, _) => Safe(() => Host.OpenPermissionSettings(permission));
            var text = new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = AppHost.PermissionText(p), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Classes = { "mut" }, FontSize = 13, Text = Loc.Get("PermAfterUpdate"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
            row.Children.Add(text);
            Grid.SetColumn(open, 1);
            row.Children.Add(open);
            PermBanner.Children.Add(new Border { Classes = { "note", "warn" }, Child = row });
        }
    }

    private void Refresh()
    {
        var host = Host;
        SyncGames();
        var summary = HomeStatus.Summarize(host.Status, host.IsPaused, DateTimeOffset.Now);
        (StateText.Text, StateDot.Fill) = summary.State switch
        {
            HomeState.Paused => (Loc.Get("StatePaused"), Brush("WarnBrush")),
            HomeState.NeedsFix => (Loc.Get("StateNeedsFix"), Brush("BadBrush")),
            _ => (Loc.Get("StateTranslating"), Brush("OkBrush")),
        };
        PauseText.Text = Loc.Get(host.IsPaused ? "ResumeButton" : "PauseButton");
        PauseIcon.Data = host.IsPaused ? PlayGlyph : PauseGlyph;
    }

    private Avalonia.Media.IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as Avalonia.Media.IBrush : null;

    private void SyncGames()
    {
        var names = Host.Settings.Profiles.Select(p => p.Name).ToList();
        var active = Host.Settings.GetActive().Name;
        _syncingGames = true;
        try
        {
            if (!names.SequenceEqual(GamePicker.Items.OfType<string>()))
            {
                GamePicker.Items.Clear();
                foreach (var name in names)
                    GamePicker.Items.Add(name);
            }
            GamePicker.SelectedItem = active;
        }
        finally
        {
            _syncingGames = false;
        }
    }

    private void OnGamePicked()
    {
        if (_syncingGames || GamePicker.SelectedItem is not string name || name == Host.Settings.GetActive().Name)
            return;
        Host.SelectProfile(name); // raises ActiveProfileChanging first, so the page commits into the game being left
        ReloadIfGameChanged();
    }

    private void ToggleTheme()
    {
        if (Application.Current is not { } app)
            return;
        Host.Settings.Theme = ThemeApplier.IsLight(app) ? AppTheme.Dark : AppTheme.Light;
        ThemeApplier.Apply(app, Host.Settings.Theme);
        Host.SaveSettings();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Safe(() =>
    {
        UpdateThemeButton();
        Refresh(); // the state dot holds a brush looked up for the old variant
    });

    private void UpdateThemeButton()
    {
        bool light = Application.Current is { } app && ThemeApplier.IsLight(app);
        ThemeIcon.Data = light ? MoonIcon : SunIcon;
        ThemeText.Text = Loc.Get(light ? "ThemeToDark" : "ThemeToLight");
    }

    private static void OpenHelp()
    {
        var repo = UpdateChecker.ReadRepositoryUrl();
        if (string.IsNullOrEmpty(repo))
            return;
        using var _ = Process.Start(new ProcessStartInfo(repo.TrimEnd('/') + "#readme") { UseShellExecute = true });
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn("Main window action failed", ex); // a UI action must not take the app down
        }
    }
}
