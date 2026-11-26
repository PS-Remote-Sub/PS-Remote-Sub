using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.App.Views.Setup;

/// <summary>
/// Step 2 "ซับในเกมเป็นภาษาอะไร" (mockup New-Setup-2): one card per <see cref="SourceLanguages.All"/> with the OCR
/// availability line, and the "แปลเป็นภาษา" selector. Next sets the active game's <c>SourceLang</c> through
/// <see cref="SettingsEdits.EditActiveGame"/> (the Games page's path) and the target through
/// <see cref="TargetLanguage.TryApply"/> (the Translation page's validation), saves, and on Windows starts the OCR
/// language download when it is missing (not awaited: the Home issue list tracks it).
/// </summary>
public sealed class LanguageStep : SetupStep
{
    private const string DownloadGlyph = "M12 4v11M7 10l5 5 5-5M5 20h14";

    /// <summary>How each language writes its own name (the cards' large line).</summary>
    private static readonly Dictionary<string, string> NativeNames = new()
    {
        ["en"] = "English",
        ["ja"] = "日本語",
        ["zh"] = "中文",
        ["ko"] = "한국어",
    };

    private readonly List<(SourceLanguage Language, OptionCard Card, TextBlock Availability)> _cards = [];
    private readonly ComboBox _target = new() { Classes = { "sel" }, Width = 220 };
    private readonly List<string> _targetCodes = [];
    private string? _shownInstalling;
    private int _availabilitySeq;

    public LanguageStep(AppHost host) : base(host)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        string current = Settings.GetActive().SourceLang;
        foreach (var language in SourceLanguages.All)
        {
            var card = new OptionCard("SetupLanguage");
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            top.Children.Add(new TextBlock
            {
                Text = NativeNames.GetValueOrDefault(language.Code, language.ThaiName),
                FontSize = 22,
                LineHeight = 30,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(card.Radio, 1);
            top.Children.Add(card.Radio);
            var availability = new TextBlock { Classes = { "sub" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
            card.Button.Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    top,
                    new TextBlock { Classes = { "mut" }, Text = language.ThaiName },
                    availability,
                },
            };
            card.Button.IsChecked = language.Code == current;
            card.Button.IsCheckedChanged += (_, _) => Safe(ShowCards);
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetColumn(card.Button, _cards.Count);
            grid.Children.Add(card.Button);
            _cards.Add((language, card, availability));
        }
        if (_cards.All(c => c.Card.Button.IsChecked != true))
            _cards[0].Card.Button.IsChecked = true; // an unknown code: the first language is offered
        ShowCards();

        FillTargets();
        var targetRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children =
            {
                new TextBlock { Classes = { "lbl" }, Text = Loc.Get("SetupTargetLabel"), VerticalAlignment = VerticalAlignment.Center },
                _target,
            },
        };

        var content = new StackPanel { Spacing = 22, Children = { grid, targetRow } };
        // The download and the Windows prompt exist only on Windows (hidden on macOS, as on the pages).
        if (Host.Platform.Kind == PlatformKind.Windows)
            content.Children.Add(InfoNote(DownloadGlyph, Loc.Get("SetupOcrNoteLead"), Loc.Get("SetupOcrNoteYes")));
        Content = content;
    }

    public override string StepName => Loc.Get("SetupStepLanguage");

    public override string Title => Loc.Get("SetupLanguageTitle");

    public override string Subtitle => Loc.Get("SetupLanguageSub");

    public override void OnShown() => _ = RefreshAvailabilityAsync();

    /// <summary>Refreshes the availability lines only when a download starts or ends (StateChanged fires often).</summary>
    protected override void OnHostStateChangedCore()
    {
        if (InstallingKey() != _shownInstalling)
            _ = RefreshAvailabilityAsync();
    }

    public override void Apply()
    {
        if (SelectedLanguage() is { } language && Settings.GetActive().SourceLang != language.Code)
            SettingsEdits.EditActiveGame(Host, p => p.SourceLang = language.Code);
        if (_target.SelectedIndex is >= 0 and var i && i < _targetCodes.Count)
            TargetLanguage.TryApply(Host, _targetCodes[i]); // a preset or the saved code: always valid
        Host.SaveSettings();
        if (SelectedLanguage() is { } chosen)
            _ = DownloadIfMissingAsync(Host, chosen);
    }

    private SourceLanguage? SelectedLanguage() =>
        _cards.FirstOrDefault(c => c.Card.Button.IsChecked == true).Language;

    private void ShowCards()
    {
        foreach (var (_, card, _) in _cards)
            card.Show();
    }

    /// <summary>
    /// ไทย first (the default), then the game languages; the saved code is kept as its own entry when it is none of
    /// these (typed on the Translation page), so leaving the selector alone never changes it.
    /// </summary>
    private void FillTargets()
    {
        var names = new List<string>();
        void Add(string code, string name)
        {
            if (_targetCodes.Contains(code))
                return;
            _targetCodes.Add(code);
            names.Add(name);
        }
        Add("th", Loc.Get("SetupTargetThai"));
        foreach (var language in SourceLanguages.All)
            Add(SourceLanguages.GoogleCode(language.Code), language.ThaiName);
        string saved = (Settings.TargetLang ?? "").Trim();
        if (saved.Length > 0 && TargetLanguage.IsValid(saved))
            Add(saved, saved);
        _target.ItemsSource = names;
        int index = _targetCodes.IndexOf(saved);
        _target.SelectedIndex = index >= 0 ? index : 0;
    }

    private string InstallingKey() =>
        string.Join(",", SourceLanguages.All.Where(Host.IsInstallingOcrLanguage).Select(l => l.Code));

    /// <summary>
    /// "พร้อมใช้" when the OCR language is installed; "ดาวน์โหลดให้อัตโนมัติ" when it is missing on Windows (or
    /// downloading: "กำลังดาวน์โหลด…"); nothing when it is missing on macOS (there is nothing to download). Never throws.
    /// </summary>
    private async Task RefreshAvailabilityAsync()
    {
        try
        {
            int seq = ++_availabilitySeq;
            _shownInstalling = InstallingKey();
            bool windows = Host.Platform.Kind == PlatformKind.Windows;
            foreach (var (language, _, line) in _cards)
            {
                if (Host.IsInstallingOcrLanguage(language))
                {
                    ShowLine(line, Loc.Get("IssueOcrInstalling"), "Text3Brush");
                    continue;
                }
                bool available;
                try
                {
                    available = await Host.IsOcrLanguageAvailableAsync(language);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Setup: OCR availability check failed: {ex.GetType().Name}");
                    line.IsVisible = false;
                    continue;
                }
                if (seq != _availabilitySeq)
                    return;
                if (Host.IsInstallingOcrLanguage(language))
                    ShowLine(line, Loc.Get("IssueOcrInstalling"), "Text3Brush");
                else if (available)
                    ShowLine(line, Loc.Get("SetupOcrReady"), "OkBrush");
                else if (windows)
                    ShowLine(line, Loc.Get("SetupOcrAuto"), "Text3Brush");
                else
                    line.IsVisible = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Setup: showing OCR availability failed: {ex.GetType().Name}"); // the lines keep their last state
        }
    }

    private static void ShowLine(TextBlock line, string text, string brush)
    {
        line.Text = text;
        line.Bind(TextBlock.ForegroundProperty, line.GetResourceObservable(brush));
        line.IsVisible = true;
    }

    /// <summary>
    /// Windows only: starts the OCR language download when it is missing and not already running. The wizard moves on
    /// at once; declining the Windows prompt just ends the install (false). Never throws.
    /// </summary>
    private static async Task DownloadIfMissingAsync(AppHost host, SourceLanguage language)
    {
        try
        {
            if (host.Platform.Kind != PlatformKind.Windows || host.IsInstallingOcrLanguage(language))
                return;
            if (await host.IsOcrLanguageAvailableAsync(language) || host.IsInstallingOcrLanguage(language))
                return;
            bool ok = await host.InstallOcrLanguageAsync(language);
            Log.Info($"Setup: OCR language {language.OcrTag} download {(ok ? "finished" : "did not finish")}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Setup: OCR language download failed: {ex.GetType().Name}");
        }
    }
}
