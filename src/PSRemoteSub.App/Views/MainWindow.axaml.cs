using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using PSRemoteSub.App.Updates;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Hub;
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
    private readonly Action<MainPage>? _openLegacySettings;
    private Func<Control>? _homeFactory;
    private Control? _home;
    private bool _exiting;
    private bool _syncingGames;

    /// <summary>For the XAML loader and designer only.</summary>
    public MainWindow() => InitializeComponent();

    internal MainWindow(AppHost host, Action<MainPage> openLegacySettings) : this()
    {
        _host = host;
        _openLegacySettings = openLegacySettings;
        foreach (var nav in NavButtons())
            nav.IsCheckedChanged += OnNavChanged;
        GamePicker.SelectionChanged += (_, _) => Safe(OnGamePicked);
        PauseButton.Click += (_, _) => Safe(() => host.SetPaused(!host.IsPaused));
        HelpButton.Click += (_, _) => Safe(OpenHelp);
        ThemeButton.Click += (_, _) => Safe(ToggleTheme);
        host.StateChanged += OnHostStateChanged;
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += OnThemeChanged;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            host.StateChanged -= OnHostStateChanged;
            if (Application.Current is { } a)
                a.ActualThemeVariantChanged -= OnThemeChanged;
        };
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
    /// Shows a page. Home lives in this window; the settings pages still open the old settings window on the matching
    /// tab (Phase 2 moves them here), and the sidebar stays on Home.
    /// </summary>
    public void ShowPage(MainPage page)
    {
        if (page != MainPage.Home)
        {
            NavHome.IsChecked = true;
            _openLegacySettings?.Invoke(page);
            return;
        }
        NavHome.IsChecked = true;
        _home ??= _homeFactory?.Invoke();
        PageHost.Content = _home;
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
        if (sender is RadioButton { IsChecked: true, Tag: MainPage page } && page != MainPage.Home)
            Safe(() => ShowPage(page));
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting)
            return;
        e.Cancel = true; // X hides to the tray; translation keeps running
        Hide();
    }

    private void OnHostStateChanged(object? sender, EventArgs e) => Safe(Refresh);

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
        Host.SelectProfile(name);
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
