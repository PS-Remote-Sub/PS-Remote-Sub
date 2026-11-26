using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PSRemoteSub.App.Views.Controls;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Pipeline;
using PSRemoteSub.Core.Settings;
using PSRemoteSub.Core.Translation.Ai;

namespace PSRemoteSub.App.Views.Pages;

/// <summary>
/// "การแปล" (mockups New-Settings-Translate and New-Settings-Translate-Ai): target language, Google/AI mode cards with
/// the translator status, the AI form (provider, key, model as numbered steps, translation style, test), "ปรับละเอียด"
/// (context, queue, parallel) and "ใช้เซิร์ฟเวอร์ของตัวเอง" (URL). Logic ported from the old settings window's
/// translation tab and the target language row of its OCR tab. Translation changes go through
/// <see cref="AppHost.ApplyTranslationSettings"/> (applies and saves). A stored API key is never shown, logged or
/// copied: it is only written through <see cref="AppHost.Secrets"/>. UI thread only.
/// </summary>
public sealed class TranslationPage : SettingsPage
{
    private const string TranslateGlyph = "M4 5h8M8 3v2M10 5c-1 4-3.5 7-6 8.5M6 9c1.2 2 3 3.5 5 4.3M13 21l4-10 4 10M14.5 17.5h5";
    private const string SparkleGlyph = "M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9zM19 16l.8 2.2L22 19l-2.2.8L19 22l-.8-2.2L16 19l2.2-.8z";
    private const string CheckGlyph = "M5 12.5l4.5 4.5L19 7.5";
    private const string InfoGlyph = "M12 21.5a9.5 9.5 0 1 0 0-19 9.5 9.5 0 0 0 0 19zM12 11v5.5M12 7.8h.01";
    private const string LockGlyph = "M6 11h12v10H6zM8.5 11V8a3.5 3.5 0 0 1 7 0v3";
    private const string WarnGlyph = "M12 3.5 2.5 20h19L12 3.5zM12 10v4.5M12 17.3h.01";
    private const string ArrowGlyph = "M9 6l6 6-6 6";
    private const string ChevronDownGlyph = "M6 9l6 6 6-6";
    private const string ChevronUpGlyph = "M6 15l6-6 6 6";
    private const string RefreshGlyph = "M20 11.5a8 8 0 1 1-2.4-5.7M20 4v5h-5";
    private const string BoltGlyph = "M13 2 4 14h7l-1 8 9-12h-7z";

    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1); // the retry countdown, as the old window

    private static readonly AiProvider[] AiProviders =
        [AiProvider.Claude, AiProvider.OpenAi, AiProvider.Gemini, AiProvider.Ollama, AiProvider.Custom];

    private static readonly TranslationStyle[] StyleChoices = [TranslationStyle.Fast, TranslationStyle.Balanced, TranslationStyle.Context];

    /// <summary>Shown as the key box's placeholder when a key is stored; the stored key itself is never shown.</summary>
    private const string StoredKeyMask = "●●●●●●●●";
    private const string AiTestSentence = "Where is the key?";
    private const int MaxTestTextLength = 200;
    private static readonly string[] AiCopyKeys =
        [AiRefiner.AiDownKey, AiRefiner.AiRateLimitedKey, AiRefiner.AiAuthKey, AiRefiner.AiModelNotFoundKey,
            AiRefiner.AiBillingKey, "AiKeyNeeded", "AiNoModel", AiRefiner.GoogleDownKey];

    /// <summary>Language codes: <c>th</c>, <c>en</c>, <c>zh-CN</c>, <c>zh-TW</c>, <c>haw</c>…</summary>

    // Language
    private readonly TextBlock _sourceLang = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox _targetLang = new() { Classes = { "in" }, MaxLength = 12, PlaceholderText = "th" };
    private readonly TextBlock _targetLangError;

    // Mode
    private readonly RadioButton _modeGoogle = new() { Classes = { "opt" }, GroupName = "TranslationMode" };
    private readonly RadioButton _modeAi = new() { Classes = { "opt" }, GroupName = "TranslationMode" };
    private readonly ModeCardParts _googleCard;
    private readonly ModeCardParts _aiCard;
    private readonly Border _statusPill = new() { Classes = { "pill", "ok" }, Height = double.NaN, MinHeight = 26, Padding = new Thickness(10, 4) };
    private readonly StrokeIcon _statusIcon = new() { Data = CheckGlyph, Size = 14, StrokeThickness = 2.2, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _statusText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
    private readonly TextBlock _privacyText = new() { Classes = { "sub" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    // AI form
    private readonly Control _aiPanel;
    private readonly TextBlock _aiStepsSub = new() { Classes = { "sub" }, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _aiProvider = new() { Classes = { "sel" } };
    private readonly Control _keyStep;
    private readonly TextBlock _modelStepNumber = new();
    private readonly TextBox _aiKey = new() { Classes = { "in" }, PasswordChar = '●', MaxLength = 400 };
    private readonly TextBox _aiModel = new() { Classes = { "in" }, MaxLength = 200 };
    private readonly Button _aiListModels;
    private readonly ComboBox _aiModelList = new() { Classes = { "sel" }, MaxDropDownHeight = 320, IsVisible = false };
    private readonly TextBlock _aiListError;
    private readonly RadioButton[] _styleButtons;
    private readonly Border _styleCustom;
    private readonly TextBlock _styleLine = new() { Classes = { "sub" }, TextWrapping = TextWrapping.Wrap };
    private readonly Button _aiTest;
    private readonly Grid _aiTestResult = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly StrokeIcon _aiTestIcon = new() { Size = 16, StrokeThickness = 2.2, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
    private readonly TextBlock _aiTestText = new() { TextWrapping = TextWrapping.Wrap };

    // Folds
    private readonly NumericUpDown _aiContext = Number(0, 10);
    private readonly NumericUpDown _aiQueue = Number(AiRefiner.MinMaxWaiting, AiRefiner.MaxMaxWaiting);
    private readonly NumericUpDown _aiParallel = Number(AiRefiner.MinMaxParallel, AiRefiner.MaxMaxParallel);
    private readonly Fold _fineFold;
    private readonly Fold _serverFold;
    private readonly TextBox _aiUrl = new() { Classes = { "in" }, MaxLength = 400 };
    private readonly Border _aiHttpWarning;

    private readonly DispatcherTimer _statusTimer;
    // Cancelled when the page is left (as the old window's close token): stops an AI test or model list in flight.
    private CancellationTokenSource _leaveCts = new();
    private int _aiTestSeq;
    private int _aiListSeq;

    public TranslationPage(AppHost host) : base(host)
    {
        // ---- language
        _targetLangError = ErrorText(Loc.Get("TargetLangInvalid"));
        _targetLang.TextChanged += (_, _) => Safe(CommitTargetLanguage);
        var sourceBox = new Border
        {
            MinHeight = 40,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 0),
            Child = _sourceLang,
        };
        sourceBox.Bind(Border.BackgroundProperty, sourceBox.GetResourceObservable("Card2Brush"));
        sourceBox.Bind(Border.BorderBrushProperty, sourceBox.GetResourceObservable("BorderBrush"));
        var langRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), ColumnSpacing = 16 };
        var sourceCol = LabeledColumn(Loc.Get("TranslateSourceLabel"), sourceBox);
        var arrow = new StrokeIcon { Data = ArrowGlyph, Size = 22, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 9) };
        arrow.Bind(StrokeIcon.ForegroundProperty, arrow.GetResourceObservable("Text3Brush"));
        var targetCol = LabeledColumn(Loc.Get("TranslateTargetLabel"), _targetLang);
        Grid.SetColumn(arrow, 1);
        Grid.SetColumn(targetCol, 2);
        langRow.Children.Add(sourceCol);
        langRow.Children.Add(arrow);
        langRow.Children.Add(targetCol);
        var langSection = Card(
            SectionHeader(Loc.Get("TranslateLangSection")),
            langRow,
            _targetLangError,
            new TextBlock { Classes = { "sub" }, TextWrapping = TextWrapping.Wrap, Text = Loc.Get("TargetLangHint") + " · " + Loc.Get("TranslateLangNote") });

        // ---- mode
        _googleCard = ModeCard(_modeGoogle, TranslateGlyph, "Google Translate", recommended: true,
            Loc.Get("TranslateGoogleGood"), Loc.Get("TranslateGoogleBad"));
        _aiCard = ModeCard(_modeAi, SparkleGlyph, "AI", recommended: false,
            Loc.Get("TranslateAiGood"), Loc.Get("TranslateAiBad"));
        _modeGoogle.IsCheckedChanged += (_, _) => Safe(() => SetTranslationMode(_modeGoogle, TranslationMode.Google));
        _modeAi.IsCheckedChanged += (_, _) => Safe(() => SetTranslationMode(_modeAi, TranslationMode.Ai));
        var cards = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 14 };
        cards.Children.Add(_modeGoogle);
        Grid.SetColumn(_modeAi, 1);
        cards.Children.Add(_modeAi);

        _statusPill.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _statusIcon, _statusText } };
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        // "สถานะการแปล" in both modes (spec).
        statusRow.Children.Add(new TextBlock { Classes = { "sub" }, Text = Loc.Get("TranslationStatusLabel"), VerticalAlignment = VerticalAlignment.Center });
        statusRow.Children.Add(_statusPill);
        var modeHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16 };
        modeHeader.Children.Add(new TextBlock { Classes = { "h2" }, Text = Loc.Get("TranslateWith"), VerticalAlignment = VerticalAlignment.Center });
        statusRow.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(statusRow, 1);
        modeHeader.Children.Add(statusRow);

        var privacy = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        var lockIcon = new StrokeIcon { Data = LockGlyph, Size = 16, VerticalAlignment = VerticalAlignment.Center };
        lockIcon.Bind(StrokeIcon.ForegroundProperty, lockIcon.GetResourceObservable("Text3Brush"));
        privacy.Children.Add(lockIcon);
        Grid.SetColumn(_privacyText, 1);
        privacy.Children.Add(_privacyText);

        var modeSection = Card(modeHeader, cards, privacy);

        // ---- AI form: steps 1-3, style, test
        _aiProvider.ItemsSource = AiProviders.Select(ProviderDisplayName).ToList();
        _aiProvider.SelectionChanged += (_, _) => Safe(SelectAiProvider);
        CommitOn(_aiKey, CommitAiFields);
        CommitOn(_aiModel, CommitAiFields);
        CommitOn(_aiUrl, CommitAiFields);
        _aiKey.TextChanged += (_, _) => Safe(UpdateHttpWarning);
        _aiUrl.TextChanged += (_, _) => Safe(UpdateHttpWarning);
        _aiModelList.SelectionChanged += (_, _) => Safe(PickOllamaModel);

        _aiListModels = IconButton(null, RefreshGlyph, "btn", () => _ = ListOllamaModelsAsync());
        _aiListModels.Padding = new Thickness(11, 0);
        ToolTip.SetTip(_aiListModels, Loc.Get("AiListModels"));
        Avalonia.Automation.AutomationProperties.SetName(_aiListModels, Loc.Get("AiListModels"));
        _aiListError = ErrorText(null);
        var modelRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        modelRow.Children.Add(_aiModel);
        Grid.SetColumn(_aiListModels, 1);
        modelRow.Children.Add(_aiListModels);
        var modelControl = new StackPanel { Spacing = 8, Children = { modelRow, _aiModelList, _aiListError } };

        _keyStep = Step(new TextBlock { Text = "2" }, FormRow(Loc.Get("AiKeyLabel"), null, _aiKey, 360));
        var steps = new StackPanel
        {
            Spacing = 18,
            Children =
            {
                Step(new TextBlock { Text = "1" }, FormRow(Loc.Get("AiProviderLabel"), null, _aiProvider, 360)),
                _keyStep,
                Step(_modelStepNumber, FormRow(Loc.Get("AiModelLabel"), Loc.Get("TranslateModelSub"), modelControl, 360)),
            },
        };

        _styleButtons = StyleChoices.Select(style =>
        {
            var b = new RadioButton { Classes = { "seg" }, GroupName = "TranslationStyle", Content = Loc.Get(StyleCopyKey(style)) };
            b.IsCheckedChanged += (_, _) => Safe(() =>
            {
                if (b.IsChecked == true)
                    ApplyStyle(style);
            });
            return b;
        }).ToArray();
        var segPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var b in _styleButtons)
            segPanel.Children.Add(b);
        _styleCustom = new Border
        {
            Classes = { "pill", "info" },
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = Loc.Get("TranslateStyleCustom") },
            IsVisible = false,
        };
        var styleRow = new WrapPanel { Orientation = Orientation.Horizontal };
        styleRow.Children.Add(new Border { Classes = { "seg" }, Child = segPanel, Margin = new Thickness(0, 0, 10, 0) });
        styleRow.Children.Add(_styleCustom);
        var styleBlock = new StackPanel
        {
            Spacing = 10,
            Children = { FormLabel(Loc.Get("TranslateStyleLabel"), Loc.Get("TranslateStyleSub")), styleRow, _styleLine },
        };

        // Not focusable: clicking it must not commit (save) a key or field that is still being typed.
        _aiTest = IconButton(Loc.Get("TranslateTest"), BoltGlyph, "btn primary", () => _ = TestAiAsync());
        _aiTest.Focusable = false;
        _aiTest.VerticalAlignment = VerticalAlignment.Top;
        _aiTestResult.Children.Add(_aiTestIcon);
        Grid.SetColumn(_aiTestText, 1);
        _aiTestResult.Children.Add(_aiTestText);
        var testRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14, Margin = new Thickness(0, 6, 0, 0) };
        testRow.Children.Add(_aiTest);
        _aiTestResult.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetColumn(_aiTestResult, 1);
        testRow.Children.Add(_aiTestResult);

        var aiSection = new Border
        {
            Classes = { "section" },
            Child = new StackPanel
            {
                Spacing = 18,
                Children =
                {
                    new StackPanel { Spacing = 2, Children = { new TextBlock { Classes = { "h2" }, Text = Loc.Get("TranslateAiSection") }, _aiStepsSub } },
                    steps,
                    Hr(),
                    styleBlock,
                    testRow,
                },
            },
        };

        // ---- folds
        _aiContext.ValueChanged += (_, _) => Safe(() => { CommitAiContext(); ShowStyle(); });
        _aiQueue.ValueChanged += (_, _) => Safe(() => { CommitAiQueue(); ShowStyle(); });
        _aiParallel.ValueChanged += (_, _) => Safe(() => { CommitAiParallel(); ShowStyle(); });
        _fineFold = new Fold(Loc.Get("TranslateFine"), Loc.Get("TranslateFineSub"), new StackPanel
        {
            Spacing = 16,
            Children =
            {
                FormRow(Loc.Get("AiContextLabel"), Loc.Get("AiContextSuffix"), _aiContext, 140),
                FormRow(Loc.Get("AiQueueLabel"), Loc.Get("AiQueueSuffix"), _aiQueue, 140),
                FormRow(Loc.Get("AiParallelLabel"), Loc.Get("AiParallelSuffix"), _aiParallel, 140),
            },
        });

        _aiHttpWarning = new Border
        {
            Classes = { "note", "bad" },
            IsVisible = false,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { new StrokeIcon { Data = WarnGlyph, Size = 18 }, new TextBlock { Text = Loc.Get("AiHttpWarning") } },
            },
        };
        _serverFold = new Fold(Loc.Get("TranslateServer"), Loc.Get("TranslateServerSub"), new StackPanel
        {
            Spacing = 10,
            Children = { FormRow(Loc.Get("AiUrlLabel"), null, _aiUrl, 420, 120), _aiHttpWarning },
        });

        _aiPanel = PageBody(aiSection, _fineFold.Root, _serverFold.Root);

        var body = PageBody(
            new TextBlock { Classes = { "h1" }, Text = Loc.Get("NavTranslation"), Margin = new Thickness(0, 0, 0, 2) },
            langSection,
            modeSection,
            _aiPanel);
        SetBody(body);

        _statusTimer = new DispatcherTimer { Interval = StatusInterval };
        _statusTimer.Tick += (_, _) => Safe(RefreshStatus);
        AttachedToVisualTree += (_, _) =>
        {
            if (_leaveCts.IsCancellationRequested)
                _leaveCts = new CancellationTokenSource();
            _statusTimer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _statusTimer.Stop();
            _leaveCts.Cancel(); // like the old window's close: a test or list in flight stops
            _aiTestSeq++;       // ... and its result is dropped (OnShown resets the buttons)
            _aiListSeq++;
        };
    }

    // ---- page hooks -----------------------------------------------------------------------------------------------

    public override void OnShown() => Safe(() =>
    {
        Loading = true;
        try
        {
            _targetLang.Text = Settings.TargetLang;
            _targetLangError.IsVisible = false;
            LoadTranslation();
        }
        finally
        {
            Loading = false;
        }
        ShowSourceLanguage();
        RefreshStatus();
    });

    /// <summary>Commits a half-typed key, model or URL (the old window did this on Closing).</summary>
    public override void CommitPending() => Safe(CommitAiFields);

    /// <summary>Status and the active game's language only: never reloads the fields (a key may be half-typed).</summary>
    protected override void OnHostStateChangedCore()
    {
        ShowSourceLanguage();
        RefreshStatus();
    }

    private void ShowSourceLanguage()
    {
        string code = Settings.GetActive().SourceLang;
        _sourceLang.Text = (SourceLanguages.All.FirstOrDefault(l => l.Code == code) ?? SourceLanguages.All[0]).ThaiName;
    }

    private void RefreshStatus() => RefreshAiStatus(Host.Status, DateTimeOffset.UtcNow);

    // ---- target language ------------------------------------------------------------------------------------------

    private void CommitTargetLanguage()
    {
        if (Loading)
            return;
        var result = TargetLanguage.TryApply(Host, _targetLang.Text);
        _targetLangError.IsVisible = result == TargetLanguageResult.Invalid;
        if (result == TargetLanguageResult.Applied)
            ScheduleSave();
    }

    // ---- translation (AI) -----------------------------------------------------------------------------------------

    /// <summary>Call with <see cref="SettingsPage.Loading"/> set: fills the page without writing back.</summary>
    private void LoadTranslation()
    {
        var t = Settings.Translation;
        _modeGoogle.IsChecked = t.Mode != TranslationMode.Ai;
        _modeAi.IsChecked = t.Mode == TranslationMode.Ai;
        ShowTranslationMode();
        _aiProvider.SelectedIndex = Math.Max(Array.IndexOf(AiProviders, t.Provider), 0);
        _aiContext.Value = Math.Clamp(t.ContextLines, 0, 10);
        _aiQueue.Value = Math.Clamp(t.MaxWaitingLines, AiRefiner.MinMaxWaiting, AiRefiner.MaxMaxWaiting);
        _aiParallel.Value = Math.Clamp(t.ParallelRequests, AiRefiner.MinMaxParallel, AiRefiner.MaxMaxParallel);
        ShowStyle();
        LoadProviderFields();
    }

    /// <summary>
    /// Call with <see cref="SettingsPage.Loading"/> set: shows the selected provider's model, URL and key state. The key
    /// box is always emptied; a stored key only shows as the <see cref="StoredKeyMask"/> placeholder.
    /// </summary>
    private void LoadProviderFields()
    {
        var provider = Settings.Translation.Provider;
        var ps = ProviderSettings(provider);
        bool ollama = provider == AiProvider.Ollama;

        _keyStep.IsVisible = !ollama;
        _aiKey.IsVisible = !ollama;
        _aiKey.Text = "";
        _aiKey.PlaceholderText = ps.ApiKeyProtected is not null ? StoredKeyMask : null;
        _modelStepNumber.Text = ollama ? "2" : "3";
        _aiStepsSub.Text = Loc.Format("TranslateAiSteps", ollama ? 2 : 3);
        _aiModel.Text = ps.Model;
        _aiUrl.Text = ps.BaseUrl;
        _aiListModels.IsVisible = ollama;
        _aiListModels.IsEnabled = true;
        _aiModelList.ItemsSource = null;
        _aiModelList.IsVisible = false;
        _aiListError.IsVisible = false;
        _aiTest.IsEnabled = true;
        _aiTestResult.IsVisible = false;
        _aiTestSeq++; // a test or list still running belongs to the previous provider: drop its result
        _aiListSeq++;
        if (provider == AiProvider.Custom && string.IsNullOrWhiteSpace(ps.BaseUrl))
            _serverFold.IsExpanded = true; // the URL is required here and lives under "ใช้เซิร์ฟเวอร์ของตัวเอง"
        UpdateHttpWarning();
    }

    /// <summary>The settings for <paramref name="p"/>; <see cref="AppSettings"/> normalization creates them all on load.</summary>
    private AiProviderSettings ProviderSettings(AiProvider p)
    {
        var providers = Settings.Translation.Providers;
        if (providers.TryGetValue(p, out var ps) && ps is not null)
            return ps;
        var preset = AiProviderPresets.Get(p);
        ps = new AiProviderSettings { BaseUrl = preset.BaseUrl, Model = preset.Model };
        providers[p] = ps;
        return ps;
    }

    private void SetTranslationMode(RadioButton button, TranslationMode mode)
    {
        // On every IsChecked change, unchecks included: the newly checked card raises its event while the old one is
        // still checked, so only the old card's later (unchecked) event leaves the looks right.
        ShowModeCards();
        if (Loading || button.IsChecked != true)
            return;
        // Rebuilds the translator handle: new generation, status cleared.
        if (SettingsEdits.SetTranslationMode(Host, mode))
            ShowTranslationMode();
    }

    private bool GoogleMode => Settings.Translation.Mode != TranslationMode.Ai;

    /// <summary>Shows the AI settings only in AI mode, and the matching privacy note. The status label never changes.</summary>
    private void ShowTranslationMode()
    {
        _aiPanel.IsVisible = !GoogleMode;
        _privacyText.Text = Loc.Get(GoogleMode ? "TranslatePrivacyGoogle" : "TranslatePrivacyAi");
        ShowModeCards();
    }

    private void ShowModeCards()
    {
        _googleCard.Show(_modeGoogle.IsChecked == true);
        _aiCard.Show(_modeAi.IsChecked == true);
    }

    private void SelectAiProvider()
    {
        if (Loading)
            return;
        if (_aiProvider.SelectedIndex is not (>= 0 and var i) || i >= AiProviders.Length)
            return;
        var provider = AiProviders[i];
        if (provider == Settings.Translation.Provider)
            return;
        // Normally LostFocus already committed the boxes when the drop-down was clicked; this also covers a change
        // without a focus move, so a half-typed key is saved under the provider it was typed for, never the new one.
        CommitAiFields();
        Settings.Translation.Provider = provider;
        Loading = true;
        try
        {
            LoadProviderFields();
        }
        finally
        {
            Loading = false;
        }
        Host.ApplyTranslationSettings();
    }

    /// <summary>
    /// LostFocus/Enter on the key, model or URL box (and page leave): writes what changed for the selected provider.
    /// The key is trimmed, protected and the box emptied; a key that is blank after trimming saves nothing (R4).
    /// </summary>
    private void CommitAiFields()
    {
        if (Loading)
            return;
        var provider = Settings.Translation.Provider;
        var ps = ProviderSettings(provider);
        bool changed = false;

        if (_aiKey.IsVisible && TypedKey() is { } key)
        {
            ps.ApiKeyProtected = Host.Secrets.Protect(key);
            changed = true;
        }
        if (!string.IsNullOrEmpty(_aiKey.Text))
            _aiKey.Text = "";
        _aiKey.PlaceholderText = ps.ApiKeyProtected is not null ? StoredKeyMask : null;

        string model = (_aiModel.Text ?? "").Trim();
        if (_aiModel.Text != model)
            _aiModel.Text = model;
        if (model != ps.Model)
        {
            ps.Model = model;
            changed = true;
        }

        string url = EffectiveUrl(provider);
        if (_aiUrl.Text != url)
            _aiUrl.Text = url;
        if (url != ps.BaseUrl)
        {
            ps.BaseUrl = url;
            changed = true;
        }

        if (changed)
            Host.ApplyTranslationSettings();
    }

    private void CommitAiContext()
    {
        if (Loading || _aiContext.Value is not { } v)
            return;
        int lines = (int)Math.Clamp(Math.Round(v), 0, 10);
        if (lines == Settings.Translation.ContextLines)
            return;
        Settings.Translation.ContextLines = lines;
        Host.ApplyTranslationSettings();
    }

    private void CommitAiQueue()
    {
        if (Loading || _aiQueue.Value is not { } v)
            return;
        int lines = (int)Math.Clamp(Math.Round(v), AiRefiner.MinMaxWaiting, AiRefiner.MaxMaxWaiting);
        if (lines == Settings.Translation.MaxWaitingLines)
            return;
        Settings.Translation.MaxWaitingLines = lines;
        Host.ApplyTranslationSettings();
    }

    private void CommitAiParallel()
    {
        if (Loading || _aiParallel.Value is not { } v)
            return;
        int requests = (int)Math.Clamp(Math.Round(v), AiRefiner.MinMaxParallel, AiRefiner.MaxMaxParallel);
        if (requests == Settings.Translation.ParallelRequests)
            return;
        Settings.Translation.ParallelRequests = requests;
        Host.ApplyTranslationSettings();
    }

    // ---- translation style (new) ----------------------------------------------------------------------------------

    /// <summary>A style button was picked: sets the three numbers to its preset, applies and saves.</summary>
    private void ApplyStyle(TranslationStyle style)
    {
        if (Loading)
            return;
        var t = Settings.Translation;
        if (TranslationStyles.Detect(t) == style)
            return;
        TranslationStyles.Apply(t, style);
        Loading = true;
        try
        {
            _aiContext.Value = t.ContextLines;
            _aiQueue.Value = t.MaxWaitingLines;
            _aiParallel.Value = t.ParallelRequests;
        }
        finally
        {
            Loading = false;
        }
        ShowStyle();
        Host.ApplyTranslationSettings();
    }

    /// <summary>
    /// Checks the style matching the saved numbers, or none plus "กำหนดเอง" when they match no preset, and writes the
    /// explanatory line from the real numbers.
    /// </summary>
    private void ShowStyle()
    {
        var t = Settings.Translation;
        var style = TranslationStyles.Detect(t);
        bool was = Loading;
        Loading = true;
        try
        {
            for (int i = 0; i < StyleChoices.Length; i++)
                _styleButtons[i].IsChecked = style == StyleChoices[i];
        }
        finally
        {
            Loading = was;
        }
        _styleCustom.IsVisible = style is null;
        _styleLine.Text = StyleLine(t.ContextLines, t.MaxWaitingLines, t.ParallelRequests);
    }

    private static string StyleLine(int context, int queue, int parallel) => string.Join(" · ",
        context <= 0 ? Loc.Get("TranslateLineNoContext") : Loc.Format("TranslateLineContext", context),
        parallel <= 1 ? Loc.Get("TranslateLineOneAtATime") : Loc.Format("TranslateLineParallel", parallel),
        Loc.Format("TranslateLineQueue", queue));

    private static string StyleCopyKey(TranslationStyle style) => style switch
    {
        TranslationStyle.Fast => "TranslateStyleFast",
        TranslationStyle.Balanced => "TranslateStyleBalanced",
        _ => "TranslateStyleContext",
    };

    // ---- fields helpers -------------------------------------------------------------------------------------------

    /// <summary>The trimmed key typed in the box, or null when it is blank (never "").</summary>
    private string? TypedKey() => (_aiKey.Text ?? "").Trim() is { Length: > 0 } key ? key : null;

    /// <summary>The URL box, trimmed; blank means the preset URL (Custom has none).</summary>
    private string EffectiveUrl(AiProvider provider)
    {
        string url = (_aiUrl.Text ?? "").Trim();
        return url.Length == 0 ? AiProviderPresets.Get(provider).BaseUrl : url;
    }

    private void UpdateHttpWarning()
    {
        bool keySet = (_aiKey.IsVisible && TypedKey() is not null)
            || ProviderSettings(Settings.Translation.Provider).ApiKeyProtected is not null;
        bool warn = keySet && IsCleartextToRemoteHost(_aiUrl.Text);
        _aiHttpWarning.IsVisible = warn;
        if (warn)
            _serverFold.IsExpanded = true;
    }

    /// <summary>
    /// True for an <c>http://</c> URL whose host is neither loopback nor a private LAN address
    /// (10/8, 172.16/12, 192.168/16): a key sent there crosses the internet unencrypted.
    /// </summary>
    private static bool IsCleartextToRemoteHost(string? text)
    {
        if (!Uri.TryCreate((text ?? "").Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
            return false;
        if (uri.IsLoopback)
            return false;
        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip))
            return true; // a host name other than localhost
        if (IPAddress.IsLoopback(ip))
            return false;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return true;
        byte[] b = ip.GetAddressBytes();
        bool lan = b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
        return !lan;
    }

    // ---- status ---------------------------------------------------------------------------------------------------

    /// <summary>The translator status (Google or AI) in the pill beside "สถานะการแปล".</summary>
    private void RefreshAiStatus(Core.Hub.PipelineStatus status, DateTimeOffset now)
    {
        bool down = status.AiError is not null && (status.AiRetryAt is not { } retry || retry > now);
        string text = Loc.Get("AiReady");
        if (down)
        {
            text = Loc.Get(AiCopyKey(status.AiError));
            if (status.AiRetryAt is { } at && status.AiError != AiRefiner.GoogleDownKey) // its copy names the retry time
                text += " · " + Loc.Format("AiRetryIn", (int)Math.Ceiling((at - now).TotalSeconds));
        }
        _statusText.Text = text;
        _statusPill.Classes.Set("ok", !down);
        _statusPill.Classes.Set("bad", down);
        _statusIcon.Data = down ? WarnGlyph : CheckGlyph;
    }

    /// <summary>A known translator copy key (AI, or Google's <c>GoogleDown</c>), or <c>AiDown</c>: never shows an unexpected string.</summary>
    private static string AiCopyKey(string? key) => key is not null && AiCopyKeys.Contains(key) ? key : AiRefiner.AiDownKey;

    // ---- test -----------------------------------------------------------------------------------------------------

    /// <summary>Sends the fixed test sentence with the fields as typed (unsaved key included) and saves nothing.</summary>
    private async Task TestAiAsync()
    {
        int seq = 0;
        try
        {
            seq = ++_aiTestSeq;
            var draft = BuildTestDraft();
            string? typedKey = _aiKey.IsVisible ? TypedKey() : null;
            string target = LanguageNames.ForTarget(
                string.IsNullOrWhiteSpace(Settings.TargetLang) ? "th" : Settings.TargetLang.Trim());
            var source = Host.AiSource;
            var ct = _leaveCts.Token;

            _aiTest.IsEnabled = false;
            ShowTestResult(Loc.Get("AiTesting"), ok: null);
            var outcome = await Task.Run(() => RunAiTestAsync(source, draft, typedKey, target, ct), CancellationToken.None);
            if (seq != _aiTestSeq)
                return;
            _aiTest.IsEnabled = true;
            if (outcome.ErrorKey is { } key)
                ShowTestResult(Loc.Get(AiCopyKey(key)) + (outcome.Detail is { } d ? "\n" + d : ""), ok: false);
            else
                ShowTestResult(Loc.Format("TranslateTestOk", outcome.Seconds.ToString("0.0", CultureInfo.InvariantCulture))
                    + " — " + outcome.Text, ok: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings: AI test display failed: {ex.GetType().Name}");
            if (seq == _aiTestSeq)
                Safe(() =>
                {
                    _aiTest.IsEnabled = true;
                    ShowTestResult(Loc.Get(AiRefiner.AiDownKey), ok: false);
                });
        }
    }

    /// <summary>The live translation settings with the selected provider's fields as currently typed.</summary>
    private TranslationSettings BuildTestDraft()
    {
        var t = Settings.Translation;
        var provider = t.Provider;
        var stored = ProviderSettings(provider);
        return new TranslationSettings
        {
            Provider = provider,
            ContextLines = t.ContextLines,
            MaxWaitingLines = t.MaxWaitingLines,
            ParallelRequests = t.ParallelRequests,
            Providers = new Dictionary<AiProvider, AiProviderSettings>
            {
                [provider] = new()
                {
                    BaseUrl = EffectiveUrl(provider),
                    Model = (_aiModel.Text ?? "").Trim(),
                    ApiKeyProtected = stored.ApiKeyProtected,
                },
            },
        };
    }

    /// <summary>Thread pool. Never throws; the outcome holds only a copy key, the time and the translated test sentence.</summary>
    private static async Task<AiTestOutcome> RunAiTestAsync(
        AiTranslatorSource source, TranslationSettings draft, string? typedKey, string target, CancellationToken ct)
    {
        try
        {
            var resolution = source.ResolveForTest(draft, typedKey);
            if (resolution.Translator is not { } translator)
                return new AiTestOutcome(resolution.ConfigErrorKey ?? AiRefiner.AiDownKey, 0, null);
            var request = new AiTranslationRequest(AiTestSentence, [], "English", target, null);
            var watch = Stopwatch.StartNew();
            string text = await translator.TranslateAsync(request, ct).ConfigureAwait(false);
            watch.Stop();
            text = text.ReplaceLineEndings(" ").Trim();
            if (text.Length > MaxTestTextLength)
                text = text[..MaxTestTextLength] + "…";
            return new AiTestOutcome(null, watch.Elapsed.TotalSeconds, text);
        }
        catch (AiTranslationException ex)
        {
            Log.Warn($"Settings: AI test failed: {ex.Kind} ({ex.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "no status"})");
            string key = ex.Kind switch
            {
                AiErrorKind.Auth => AiRefiner.AiAuthKey,
                AiErrorKind.ModelNotFound => AiRefiner.AiModelNotFoundKey,
                AiErrorKind.RateLimited => AiRefiner.AiRateLimitedKey,
                AiErrorKind.Billing => AiRefiner.AiBillingKey,
                _ => AiRefiner.AiDownKey,
            };
            // Diagnostic detail for this test only (a fixed sample sentence, so nothing private): never logged.
            string? detail = (ex.StatusCode, ex.ProviderMessage) switch
            {
                (null, null) => null,
                ({ } code, null) => $"HTTP {code.ToString(CultureInfo.InvariantCulture)}",
                (null, { } msg) => msg,
                ({ } code, { } msg) => $"HTTP {code.ToString(CultureInfo.InvariantCulture)} — {msg}",
            };
            return new AiTestOutcome(key, 0, null, detail);
        }
        catch (Exception ex)
        {
            // Type only: a message could carry the URL or other settings.
            Log.Warn($"Settings: AI test failed: {ex.GetType().Name}");
            return new AiTestOutcome(AiRefiner.AiDownKey, 0, null);
        }
    }

    private void ShowTestResult(string text, bool? ok)
    {
        _aiTestText.Text = text;
        _aiTestIcon.Data = ok switch { true => CheckGlyph, false => WarnGlyph, null => InfoGlyph };
        string brush = ok switch { true => "OkBrush", false => "BadBrush", null => "Text2Brush" };
        _aiTestText.Bind(TextBlock.ForegroundProperty, _aiTestText.GetResourceObservable(brush));
        _aiTestIcon.Bind(StrokeIcon.ForegroundProperty, _aiTestIcon.GetResourceObservable(brush));
        _aiTestResult.IsVisible = true;
    }

    // ---- Ollama model list ----------------------------------------------------------------------------------------

    /// <summary>Ollama: fills the model dropdown from <c>/api/tags</c> at the URL as typed.</summary>
    private async Task ListOllamaModelsAsync()
    {
        int seq = 0;
        try
        {
            seq = ++_aiListSeq;
            _aiListError.IsVisible = false;
            if (!Uri.TryCreate(EffectiveUrl(AiProvider.Ollama), UriKind.Absolute, out var baseUrl)
                || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
            {
                ShowListError(AiRefiner.AiDownKey);
                return;
            }
            var http = Host.AiHttp;
            var ct = _leaveCts.Token;
            _aiListModels.IsEnabled = false;
            var names = await Task.Run(async () =>
            {
                try
                {
                    return await OllamaModels.ListAsync(http, baseUrl, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Settings: Ollama model list failed: {(ex is AiTranslationException ai ? ai.Kind.ToString() : ex.GetType().Name)}");
                    return null;
                }
            }, CancellationToken.None);
            if (seq != _aiListSeq)
                return;
            _aiListModels.IsEnabled = true;
            if (names is null || names.Count == 0)
            {
                ShowListError(names is null ? AiRefiner.AiDownKey : "AiNoModel");
                return;
            }
            string current = (_aiModel.Text ?? "").Trim();
            Loading = true;
            try
            {
                _aiModelList.ItemsSource = names.ToList();
                _aiModelList.SelectedItem = names.Contains(current) ? current : null;
            }
            finally
            {
                Loading = false;
            }
            _aiModelList.IsVisible = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings: Ollama model list display failed: {ex.GetType().Name}");
            if (seq == _aiListSeq)
                Safe(() => _aiListModels.IsEnabled = true);
        }
    }

    private void ShowListError(string key)
    {
        _aiListError.Text = Loc.Get(key);
        _aiListError.IsVisible = true;
    }

    private void PickOllamaModel()
    {
        if (Loading || _aiModelList.SelectedItem is not string name)
            return;
        _aiModel.Text = name;
        CommitAiFields();
    }

    private sealed record AiTestOutcome(string? ErrorKey, double Seconds, string? Text, string? Detail = null);

    // ---- building blocks ------------------------------------------------------------------------------------------

    private static string ProviderDisplayName(AiProvider p) => p switch
    {
        AiProvider.Claude => "Claude",
        AiProvider.OpenAi => "OpenAI",
        AiProvider.Gemini => "Gemini",
        AiProvider.Ollama => "Ollama",
        _ => Loc.Get("AiProviderCustom"),
    };

    private static NumericUpDown Number(int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = 1,
        FormatString = "0",
        Width = 140,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    /// <summary>A numbered step: the round number badge, then a form row.</summary>
    private static Grid Step(TextBlock number, Control row)
    {
        number.FontWeight = FontWeight.SemiBold;
        number.HorizontalAlignment = HorizontalAlignment.Center;
        number.VerticalAlignment = VerticalAlignment.Center;
        var badge = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
            Child = number,
        };
        badge.Bind(Border.BackgroundProperty, badge.GetResourceObservable("Card2Brush"));
        badge.Bind(Border.BorderBrushProperty, badge.GetResourceObservable("Border2Brush"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*"), ColumnSpacing = 16 };
        grid.Children.Add(badge);
        Grid.SetColumn(row, 1);
        grid.Children.Add(row);
        return grid;
    }

    /// <summary>A small semibold caption above a control (the language row).</summary>
    private static StackPanel LabeledColumn(string caption, Control control) => new()
    {
        Spacing = 6,
        Children =
        {
            new TextBlock { Classes = { "sub" }, FontWeight = FontWeight.SemiBold, Text = caption },
            control,
        },
    };

    /// <summary>The visual parts of a mode card that follow its checked state (radio dot, icon tint).</summary>
    private sealed class ModeCardParts(Ellipse ring, Ellipse dot, StrokeIcon icon)
    {
        public void Show(bool on)
        {
            ring.Bind(Shape.StrokeProperty, ring.GetResourceObservable(on ? "AccentBrush" : "Text3Brush"));
            dot.IsVisible = on;
            icon.Bind(StrokeIcon.ForegroundProperty, icon.GetResourceObservable(on ? "AccentBrush" : "Text2Brush"));
        }
    }

    private static ModeCardParts ModeCard(RadioButton button, string glyph, string title, bool recommended, string good, string bad)
    {
        var ring = new Ellipse { StrokeThickness = 2 };
        var dot = new Ellipse { Width = 10, Height = 10, IsVisible = false };
        dot.Bind(Shape.FillProperty, dot.GetResourceObservable("AccentBrush"));
        var radio = new Panel { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0), Children = { ring, dot } };

        var icon = new StrokeIcon { Data = glyph, Size = 20, VerticalAlignment = VerticalAlignment.Center };
        var head = new WrapPanel { Orientation = Orientation.Horizontal };
        icon.Margin = new Thickness(0, 0, 8, 0);
        head.Children.Add(icon);
        head.Children.Add(new TextBlock { Classes = { "lbl" }, FontSize = 17, Text = title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        if (recommended)
            head.Children.Add(new Border { Classes = { "pill", "info" }, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = Loc.Get("TranslateRecommended") } });

        var lines = new StackPanel { Spacing = 2, Children = { CardLine(CheckGlyph, "OkBrush", good), CardLine(InfoGlyph, "Text3Brush", bad) } };
        var content = new StackPanel { Spacing = 8, Children = { head, lines } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        grid.Children.Add(radio);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        button.Content = grid;
        button.VerticalAlignment = VerticalAlignment.Stretch;
        return new ModeCardParts(ring, dot, icon);
    }

    private static Grid CardLine(string glyph, string brush, string text)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        var icon = new StrokeIcon { Data = glyph, Size = 16, StrokeThickness = 2.2, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        icon.Bind(StrokeIcon.ForegroundProperty, icon.GetResourceObservable(brush));
        row.Children.Add(icon);
        var block = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(block, 1);
        row.Children.Add(block);
        return row;
    }

    /// <summary>
    /// A collapsible card (mockup: a card-styled button with title, sub line and chevron; the body opens below it).
    /// </summary>
    private sealed class Fold
    {
        private readonly StrokeIcon _chevron = new() { Data = ChevronDownGlyph, Size = 18, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border _body;
        private readonly Button _header;

        public Fold(string title, string sub, Control body)
        {
            var titleBlock = new TextBlock { Classes = { "lbl" }, Text = title, TextWrapping = TextWrapping.Wrap };
            titleBlock.Bind(TextBlock.ForegroundProperty, titleBlock.GetResourceObservable("TextBrush"));
            var subBlock = new TextBlock { Classes = { "sub" }, Text = sub, TextWrapping = TextWrapping.Wrap };
            _chevron.Bind(StrokeIcon.ForegroundProperty, _chevron.GetResourceObservable("Text3Brush"));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
            row.Children.Add(new StackPanel { Children = { titleBlock, subBlock } });
            Grid.SetColumn(_chevron, 1);
            row.Children.Add(_chevron);

            _header = new Button
            {
                Classes = { "btn", "ghost" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(22, 16),
                CornerRadius = new CornerRadius(14),
                FontWeight = FontWeight.Normal,
                Content = row,
            };
            _header.Click += (_, _) => Safe(() => IsExpanded = !IsExpanded);
            _body = new Border { Padding = new Thickness(22, 4, 22, 22), Child = body, IsVisible = false };
            Root = new Border { Classes = { "card" }, Child = new StackPanel { Children = { _header, _body } } };
            Avalonia.Automation.AutomationProperties.SetName(_header, title);
        }

        public Border Root { get; }

        public bool IsExpanded
        {
            get => _body.IsVisible;
            set
            {
                _body.IsVisible = value;
                _chevron.Data = value ? ChevronUpGlyph : ChevronDownGlyph;
            }
        }
    }

    private static Border Card(params Control[] children)
    {
        var stack = new StackPanel { Spacing = 14 };
        foreach (var child in children)
            stack.Children.Add(child);
        return new Border { Classes = { "section" }, Child = stack };
    }

    private static Border Hr() => new() { Classes = { "hr" } };

    /// <summary>A styled button with an optional icon.</summary>
    private static Button IconButton(string? text, string? glyph, string classes, Action action)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (glyph is not null)
            row.Children.Add(new StrokeIcon { Data = glyph, Size = 18, VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(text))
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = glyph is null ? text : row };
        button.Classes.AddRange(classes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        button.Click += (_, _) => Safe(action);
        return button;
    }

    private static TextBlock ErrorText(string? text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsVisible = false, FontSize = 13 };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable("BadBrush"));
        return block;
    }
}
