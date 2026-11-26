using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PSRemoteSub.App.Views.Controls;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Input;
using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App.Views.Pages;

/// <summary>
/// Home: whether subtitles are being translated, the latest line, the state of each part, shortcuts — or, when
/// something blocks translation, the problems in fix order with a button for each fix the app can do.
/// </summary>
public partial class HomePage : UserControl
{
    private static readonly TimeSpan FreshFrame = TimeSpan.FromSeconds(5);
    private const string CheckGlyph = "M5 12.5l4.5 4.5L19 7.5";
    private const string WarnGlyph = "M12 3 2 20h20zM12 10v4.5M12 17.5h.01";
    private const string RegionGlyph = "M4 8V5a1 1 0 0 1 1-1h3M16 4h3a1 1 0 0 1 1 1v3M20 16v3a1 1 0 0 1-1 1h-3M8 20H5a1 1 0 0 1-1-1v-3M8 12h8";
    private const string PhoneGlyph = "M8 2h8a1.5 1.5 0 0 1 1.5 1.5v17A1.5 1.5 0 0 1 16 22H8a1.5 1.5 0 0 1-1.5-1.5v-17A1.5 1.5 0 0 1 8 2zM11 18.5h2";
    private const string TvGlyph = "M3 7h18v11H3zM8 21h8M8.5 3 12 6.5 15.5 3";
    private const string PauseGlyph = "M9 5v14M15 5v14";
    private const string PlayGlyph = "M8 5l11 7-11 7z";

    private readonly AppHost? _host;
    private readonly Action<MainPage>? _openSettings;
    private readonly DispatcherTimer _clock;
    private int _refreshPending;
    private string? _shown;

    /// <summary>For the XAML loader and designer only.</summary>
    public HomePage()
    {
        InitializeComponent();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
    }

    internal HomePage(AppHost host, Action<MainPage> openSettings) : this()
    {
        _host = host;
        _openSettings = openSettings;
        _clock.Tick += (_, _) => Safe(Rebuild);
        AttachedToVisualTree += (_, _) =>
        {
            host.StateChanged += OnStateChanged;
            host.Hub.Changed += OnHubChanged;
            if (Application.Current is { } app)
                app.ActualThemeVariantChanged += OnStateChanged;
            _clock.Start();
            Safe(Rebuild);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            host.StateChanged -= OnStateChanged;
            host.Hub.Changed -= OnHubChanged;
            if (Application.Current is { } app)
                app.ActualThemeVariantChanged -= OnStateChanged;
            _clock.Stop();
        };
    }

    private AppHost Host => _host ?? throw new InvalidOperationException("No host");

    private void OnStateChanged(object? sender, EventArgs e) => QueueRebuild();

    /// <summary>Hub events come from worker threads: post one rebuild to the UI thread, coalescing bursts.</summary>
    private void OnHubChanged(object? sender, HubEvent e) => QueueRebuild();

    private void QueueRebuild()
    {
        if (Interlocked.Exchange(ref _refreshPending, 1) == 1)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshPending, 0);
            Safe(Rebuild);
        });
    }

    private void Rebuild()
    {
        var host = Host;
        var summary = HomeStatus.Summarize(host.Status, host.IsPaused, DateTimeOffset.Now);
        // Rebuilding drops hover and focus, so only rebuild when something shown has changed.
        var shown = Signature(host, summary);
        if (shown == _shown)
            return;
        _shown = shown;
        Root.Children.Clear();
        if (summary.State == HomeState.NeedsFix)
            BuildIssues(host, summary);
        else
            BuildNormal(host, summary.State == HomeState.Paused);
    }

    /// <summary>Everything the page shows, as one string; equal strings mean nothing on screen would change.</summary>
    private string Signature(AppHost host, HomeSummary summary)
    {
        var profile = host.Settings.GetActive();
        var last = host.Hub.GetHistory().LastOrDefault();
        var frame = host.LatestFrame;
        bool fresh = host.LastFrameAt is { } at && DateTimeOffset.Now - at < FreshFrame;
        var lang = SourceLanguages.All.FirstOrDefault(l => l.Code == profile.SourceLang);
        return string.Join('|',
            ActualThemeVariant, summary.State, string.Join(',', summary.Issues), host.Status.AiError,
            profile.Name, profile.SourceLang, host.Settings.TargetLang, host.PictureSource, host.IsCaptureAvailable,
            last?.Id, last?.Translated, frame?.Width, frame?.Height, fresh, host.Status.RemotePlayFound,
            host.Settings.Translation.Mode, host.Settings.Translation.Provider, host.Settings.OverlayEnabled,
            host.IsWebRunning, host.WebClients, host.Settings.Tv.Enabled,
            host.Settings.Hotkeys.GetValueOrDefault(HotkeyAction.TogglePause),
            lang is not null && host.IsInstallingOcrLanguage(lang));
    }

    // ---------------------------------------------------------------- normal layout (New-Home-A)

    private void BuildNormal(AppHost host, bool paused)
    {
        Root.Children.Add(Heading(Loc.Get("HomeTitle"), Loc.Get(paused ? "HomePausedLine" : "HomeReadyLine")));
        Root.Children.Add(StatusCard(host, paused));
        if (LatestCard(host) is { } latest)
            Root.Children.Add(latest);
        Root.Children.Add(StatusGrid(host));
        Root.Children.Add(Shortcuts(host));
    }

    private Control StatusCard(AppHost host, bool paused)
    {
        var profile = host.Settings.GetActive();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleRow.Children.Add(new Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center, Fill = Res(paused ? "WarnBrush" : "OkBrush") });
        titleRow.Children.Add(Text(Loc.Get(paused ? "HomePausedTitle" : "HomeTranslatingTitle"), "h2"));
        var left = new StackPanel { Spacing = 4 };
        left.Children.Add(titleRow);
        left.Children.Add(Text(Loc.Format("HomeGameLine", profile.Name,
            HomeIssueCopy.LanguageName(profile.SourceLang), TargetName(host.Settings.TargetLang)), "mut"));

        var pause = Button(Loc.Get(paused ? "ResumeButton" : "PauseButton"), paused ? PlayGlyph : PauseGlyph,
            () => host.SetPaused(!host.IsPaused), paused ? "btn primary" : "btn");
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(pause);
        var caps = HotkeyLabels.KeyCaps(host.Settings.Hotkeys.GetValueOrDefault(HotkeyAction.TogglePause), host.Platform.Kind);
        if (caps.Count > 0)
        {
            right.Children.Add(new TextBlock { Classes = { "sub" }, Text = Loc.Get("HomeOrPress"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            foreach (var cap in caps)
                right.Children.Add(Kbd(cap));
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return Card(grid, new Thickness(22, 20));
    }

    private Control? LatestCard(AppHost host)
    {
        var last = host.Hub.GetHistory().LastOrDefault();
        if (last is null)
            return null;
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(Text(Loc.Get("HomeLatest"), "sub"));
        stack.Children.Add(new TextBlock { Classes = { "mut" }, Text = last.Source, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(last.Translated))
            stack.Children.Add(new TextBlock { Text = last.Translated, FontSize = 22, LineHeight = 32, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        return Card(stack, new Thickness(22, 18));
    }

    private Control StatusGrid(AppHost host)
    {
        var status = host.Status;
        var profile = host.Settings.GetActive();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };

        // Picture source
        var frame = host.LatestFrame;
        bool fresh = host.LastFrameAt is { } at && DateTimeOffset.Now - at < FreshFrame;
        string sourceName = host.PictureSource == PictureSource.CaptureCard ? "Capture card" : "PS Remote Play";
        string frameLine = frame is null ? "" : Loc.Format(fresh ? "HomeFrameFresh" : "HomeFrameSize", frame.Width, frame.Height);
        AddTile(grid, 0, StatusTile(Loc.Get("HomeCardSource"),
            status.RemotePlayFound ? Loc.Get("HomeSourceSeen") : Loc.Get("HomeSourceNone"), status.RemotePlayFound,
            sourceName, frameLine));

        // OCR
        bool ocrOk = !status.OcrUnavailable;
        AddTile(grid, 1, StatusTile(Loc.Get("HomeCardOcr"), Loc.Get(ocrOk ? "HomeReady" : "HomeNeedsFix"), ocrOk,
            Loc.Format("HomeOcrLang", HomeIssueCopy.LanguageName(profile.SourceLang)),
            Loc.Get(ocrOk ? "HomeOcrOk" : "HomeOcrMissing")));

        // Translation
        bool aiError = HomeStatus.Summarize(status, false, DateTimeOffset.Now).Issues.Contains(HomeIssue.AiError);
        AddTile(grid, 2, StatusTile(Loc.Get("HomeCardTranslate"), Loc.Get(aiError ? "HomeNeedsFix" : "HomeReady"), !aiError,
            ProviderName(host.Settings.Translation),
            host.Settings.Translation.Mode == TranslationMode.Google ? Loc.Get("HomeGoogleNote") : Loc.Get("HomeAiNote")));

        // Where subtitles show
        var places = new List<string>();
        if (host.Settings.OverlayEnabled) places.Add(Loc.Get("HomeShownPc"));
        if (host.IsWebRunning) places.Add(Loc.Get("HomeShownPhone"));
        if (host.Settings.Tv.Enabled) places.Add(Loc.Get("HomeShownTv"));
        AddTile(grid, 3, StatusTile(Loc.Get("HomeCardShown"),
            places.Count == 0 ? Loc.Get("HomeShownNone") : Loc.Format("HomeShownCount", places.Count), places.Count > 0,
            string.Join(" + ", places),
            host.WebClients > 0 ? Loc.Format("HomePhoneViewers", host.WebClients) : ""));
        return grid;
    }

    private static void AddTile(Grid grid, int column, Control tile)
    {
        tile.Margin = new Thickness(column == 0 ? 0 : 6, 0, column == 3 ? 0 : 6, 0);
        Grid.SetColumn(tile, column);
        grid.Children.Add(tile);
    }

    private Control StatusTile(string label, string pillText, bool ok, string title, string detail)
    {
        var stack = new StackPanel { Spacing = 8 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(new TextBlock { Classes = { "sub" }, Text = label, VerticalAlignment = VerticalAlignment.Center });
        var pill = Pill(pillText, ok ? "ok" : "warn", ok ? CheckGlyph : WarnGlyph);
        Grid.SetColumn(pill, 1);
        head.Children.Add(pill);
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock { Classes = { "lbl" }, Text = title, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(detail))
            stack.Children.Add(new TextBlock { Classes = { "sub" }, Text = detail, TextWrapping = TextWrapping.Wrap });
        return Card(stack, new Thickness(16));
    }

    private Control Shortcuts(AppHost host)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        AddShortcut(grid, 0, RegionGlyph, Loc.Get("HomeRegionTitle"), Loc.Get("HomeRegionSub"), () => host.RequestRegionSelection(null));
        AddShortcut(grid, 1, PhoneGlyph, Loc.Get("HomePhoneTitle"), Loc.Get("HomePhoneSub"), () => _openSettings?.Invoke(MainPage.Phone));
        AddShortcut(grid, 2, TvGlyph, Loc.Get("HomeTvTitle"),
            Loc.Get(host.Settings.Tv.Enabled ? "HomeTvSubOn" : "HomeTvSubOff"), () => _openSettings?.Invoke(MainPage.Tv));
        return grid;
    }

    private void AddShortcut(Grid grid, int column, string glyph, string title, string sub, Action action)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new StrokeIcon { Data = glyph, Size = 22, Foreground = Res("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left });
        stack.Children.Add(new TextBlock { Classes = { "lbl" }, Text = title });
        stack.Children.Add(new TextBlock { Classes = { "sub" }, Text = sub, TextWrapping = TextWrapping.Wrap });
        var button = new Button
        {
            Classes = { "btn" },
            Content = stack,
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(14),
            Background = Res("CardBrush"),
            BorderBrush = Res("BorderBrush"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontWeight = FontWeight.Normal,
            Margin = new Thickness(column == 0 ? 0 : 6, 0, column == 2 ? 0 : 6, 0),
        };
        button.Click += (_, _) => Safe(action);
        Grid.SetColumn(button, column);
        grid.Children.Add(button);
    }

    // ---------------------------------------------------------------- issues layout (New-Home-Issues)

    private void BuildIssues(AppHost host, HomeSummary summary)
    {
        Root.Children.Add(Heading(Loc.Get("HomeTitle"), Loc.Get("HomeIssuesLine")));

        var list = new StackPanel { Spacing = 0 };
        var banner = new Border
        {
            Background = Res("WarnBgBrush"),
            CornerRadius = new CornerRadius(14, 14, 0, 0),
            Padding = new Thickness(22, 16),
        };
        var bannerText = new StackPanel { Spacing = 2 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleRow.Children.Add(new StrokeIcon { Data = WarnGlyph, Size = 20, Foreground = Res("WarnBrush"), VerticalAlignment = VerticalAlignment.Center });
        titleRow.Children.Add(Text(Loc.Format("HomeIssuesTitle", summary.Issues.Count), "h2"));
        bannerText.Children.Add(titleRow);
        bannerText.Children.Add(Text(Loc.Get("HomeIssuesHint"), "mut"));
        banner.Child = bannerText;
        list.Children.Add(banner);

        for (int i = 0; i < summary.Issues.Count; i++)
        {
            if (i > 0)
                list.Children.Add(new Border { Classes = { "hr" } });
            list.Children.Add(IssueRow(host, i + 1, summary.Issues[i]));
        }
        Root.Children.Add(Card(list, new Thickness(0)));

        Root.Children.Add(ReadyList(host, summary));
    }

    private Control IssueRow(AppHost host, int number, HomeIssue issue)
    {
        var (title, body) = HomeIssueCopy.For(issue, host);
        var badge = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = Res("WarnBgBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = number.ToString(), FontWeight = FontWeight.Bold, FontSize = 14, Foreground = Res("WarnBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock { Classes = { "lbl" }, Text = title, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(body))
            text.Children.Add(new TextBlock { Classes = { "mut" }, Text = body, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        var actions = IssueActions(host, issue);
        if (actions.Children.Count > 0)
            text.Children.Add(actions);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(22, 18) };
        row.Children.Add(badge);
        text.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private StackPanel IssueActions(AppHost host, HomeIssue issue)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        bool card = host.PictureSource == PictureSource.CaptureCard;
        switch (issue)
        {
            case HomeIssue.NoSource when card:
                actions.Children.Add(Button(Loc.Get("IssueOpenCapture"), null, host.RequestCaptureWindow, "btn primary sm"));
                break;
            case HomeIssue.NoSource when host.IsCaptureAvailable:
                actions.Children.Add(Button(Loc.Get("IssueUseCapture"), null, () => host.SetPictureSource(PictureSource.CaptureCard), "btn sm"));
                break;
            case HomeIssue.OcrUnavailable:
                var lang = SourceLanguages.All.FirstOrDefault(l => l.Code == host.Settings.GetActive().SourceLang);
                if (lang is null)
                    break;
                bool installing = host.IsInstallingOcrLanguage(lang);
                var install = Button(installing ? Loc.Get("IssueOcrInstalling") : Loc.Format("IssueOcrButton", lang.ThaiName),
                    null, () => _ = InstallAsync(host, lang), "btn primary sm");
                install.IsEnabled = !installing;
                actions.Children.Add(install);
                break;
            case HomeIssue.NoRegion:
                actions.Children.Add(Button(Loc.Get("HomeRegionTitle"), RegionGlyph, () => host.RequestRegionSelection(null), "btn primary sm"));
                break;
            case HomeIssue.AiError:
                actions.Children.Add(Button(Loc.Get("IssueAiButton"), null, () => _openSettings?.Invoke(MainPage.Translation), "btn sm"));
                break;
        }
        return actions;
    }

    private static async Task InstallAsync(AppHost host, SourceLanguage lang)
    {
        try
        {
            await host.InstallOcrLanguageAsync(lang); // AppHost raises StateChanged and notifies the outcome
        }
        catch (Exception ex)
        {
            Log.Warn("Home: OCR language install failed", ex);
        }
    }

    private Control ReadyList(AppHost host, HomeSummary summary)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Text(Loc.Get("HomeReadyHeader"), "sub"));
        if (!summary.Issues.Contains(HomeIssue.AiError))
            stack.Children.Add(ReadyItem(Loc.Format("HomeReadyTranslate", ProviderName(host.Settings.Translation))));
        if (host.Settings.OverlayEnabled)
            stack.Children.Add(ReadyItem(Loc.Get("HomeReadyOverlay")));
        if (!summary.Issues.Contains(HomeIssue.NoRegion))
            stack.Children.Add(ReadyItem(Loc.Get("HomeReadyRegion")));
        return stack;
    }

    private Control ReadyItem(string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new StrokeIcon { Data = CheckGlyph, Size = 18, StrokeThickness = 2.2, Foreground = Res("OkBrush"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    // ---------------------------------------------------------------- helpers

    private static string TargetName(string code) => code == "th" ? "ไทย" : code;

    private static string ProviderName(TranslationSettings t) => t.Mode == TranslationMode.Google
        ? "Google Translate"
        : t.Provider switch
        {
            AiProvider.OpenAi => "OpenAI",
            AiProvider.Custom => "AI",
            var p => p.ToString(),
        };

    private static Control Heading(string title, string line)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(Text(title, "h1"));
        stack.Children.Add(Text(line, "mut"));
        return stack;
    }

    private static TextBlock Text(string text, string cls) =>
        new() { Classes = { cls }, Text = text, TextWrapping = TextWrapping.Wrap };

    private static Border Card(Control child, Thickness padding) =>
        new() { Classes = { "card" }, Padding = padding, Child = child, ClipToBounds = true };

    private static Border Kbd(string cap) => new() { Classes = { "kbd" }, Child = new TextBlock { Text = cap } };

    private static Border Pill(string text, string kind, string glyph)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new StrokeIcon { Data = glyph, Size = 14, StrokeThickness = 2.2, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text });
        return new Border { Classes = { "pill", kind }, Child = row };
    }

    private Button Button(string text, string? glyph, Action action, string classes)
    {
        object content = text;
        if (glyph is not null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new StrokeIcon { Data = glyph, Size = 16, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            content = row;
        }
        var button = new Button { Content = content };
        button.Classes.AddRange(classes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        button.Click += (_, _) => Safe(action);
        return button;
    }

    private IBrush? Res(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn("Home page action failed", ex); // a UI action must not take the app down
        }
    }
}
