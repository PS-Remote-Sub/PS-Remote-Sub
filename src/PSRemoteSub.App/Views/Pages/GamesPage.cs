using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PSRemoteSub.App.Views.Controls;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Layout;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App.Views.Pages;

/// <summary>
/// "เกมและโซนซับ" (mockup New-Settings-Games): the game list (add, rename, delete, pick), the whole-game settings
/// (title for AI, source language with the OCR language install when it is missing, minimum display time, library) and
/// the zones of the active game. Logic ported from the old settings window's profiles and OCR tabs. Every game switch
/// (pick, add, rename, delete) goes through <see cref="AppHost"/> so <see cref="AppHost.ActiveProfileChanging"/> commits
/// the open page into the game being left. UI thread only.
/// </summary>
public sealed class GamesPage : SettingsPage
{
    private const string GamepadGlyph = "M6.5 7.5h11a4.5 4.5 0 0 1 4.5 4.5v.5a4 4 0 0 1-7.2 2.4L14 13.5h-4l-.8 1.4A4 4 0 0 1 2 12.5V12a4.5 4.5 0 0 1 4.5-4.5zM7.5 10.5v3M6 12h3M15.5 11h.01M17.5 13h.01";
    private const string PlusGlyph = "M12 5v14M5 12h14";
    private const string PencilGlyph = "M4 20h4L19 9l-4-4L4 16zM13.5 6.5l4 4";
    private const string TrashGlyph = "M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3";
    private const string FolderGlyph = "M3 6.5A1.5 1.5 0 0 1 4.5 5H10l2 2.5h7.5A1.5 1.5 0 0 1 21 9v9.5a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18.5z";
    private const string CheckCircleGlyph = "M12 21.5a9.5 9.5 0 1 0 0-19 9.5 9.5 0 0 0 0 19zM8 12.3l2.7 2.7L16 9.6";
    private const string WarnGlyph = "M12 3.5 2.5 20h19L12 3.5zM12 10v4.5M12 17.3h.01";
    private const string FrameGlyph = "M4 9V4h5M15 4h5v5M20 15v5h-5M9 20H4v-5";

    /// <summary>Below this content width the game list goes above the editor instead of beside it.</summary>
    private const double NarrowWidth = 700;

    private static readonly PreprocessFilter[] Filters = [PreprocessFilter.Normal, PreprocessFilter.WhiteText, PreprocessFilter.DarkText];
    private static readonly OverlayPlacement[] Placements = [OverlayPlacement.Custom, OverlayPlacement.Fit];

    // Layout
    private readonly Grid _layout;
    private readonly Control _listColumn;
    private readonly Control _editor;
    private bool? _narrow;

    // Game list
    private readonly StackPanel _gameList = new() { Spacing = 8 };
    private readonly Button _addGame;
    private readonly StackPanel _addPanel = new() { Spacing = 8, IsVisible = false };
    private readonly TextBox _newName = new() { Classes = { "in" }, MaxLength = 40 };
    private readonly TextBlock _addError = ErrorText();

    // Whole game
    private readonly Button _rename;
    private readonly Button _delete;
    private readonly StackPanel _renamePanel = new() { Spacing = 8, IsVisible = false };
    private readonly TextBox _renameBox = new() { Classes = { "in" }, MaxLength = 40 };
    private readonly TextBlock _renameError = ErrorText();
    private readonly TextBox _gameTitle = new() { Classes = { "in" }, MaxLength = 80 };
    private readonly ComboBox _sourceLang = new() { Classes = { "sel" } };
    private readonly Border _ocrNote;
    private readonly TextBlock _ocrNoteText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _ocrInstall;
    private readonly TextBlock _ocrMessage = new() { Classes = { "sub" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly WrapPanel _minPanel = new() { Orientation = Orientation.Horizontal };
    private readonly ComboBox _library = new() { Classes = { "sel" }, MaxDropDownHeight = 320 };
    private readonly Button _openLibrary;
    private readonly TextBlock _libraryNote = new() { Classes = { "sub" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    // Zones
    private readonly WrapPanel _zoneTabs = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _boxLines = new() { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _zoneName = new() { Classes = { "in" }, MaxLength = 40 };
    private readonly Button _deleteZone;
    private readonly ComboBox _filter = new() { Classes = { "sel" } };
    private readonly ComboBox _placement = new() { Classes = { "sel" } };
    private readonly ToggleSwitch _sendToWeb = new() { Classes = { "tg" } };
    private readonly ToggleSwitch _learn = new() { Classes = { "tg" } };
    private readonly ToggleSwitch _speakerLinesOnly = new() { Classes = { "tg" } };
    private readonly ToggleSwitch _trigger = new() { Classes = { "tg" } };
    private readonly TextBox _triggerWords = new() { Classes = { "in" }, MaxLength = 200 };

    // State
    private GameProfile? _loadedProfile;   // the profile the editor was filled from
    private string? _shownProfiles;        // names + active, as the game list shows them
    private double[] _minChoices = [];
    private string? _zoneId;
    private string? _shownZones;
    private string? _shownBoxes;
    private GameProfile? _textsProfile;
    private string? _textsZoneId;
    private string?[] _libraryGames = [null];
    private int _shownLibraryListVersion = -1;
    private string _shownInstalling = "";
    private int _ocrSeq;

    public GamesPage(AppHost host) : base(host)
    {
        // ---- game list column
        _addGame = IconButton(Loc.Get("GamesAdd"), PlusGlyph, "btn", ShowAddPanel);
        _addGame.HorizontalAlignment = HorizontalAlignment.Stretch;
        _addGame.HorizontalContentAlignment = HorizontalAlignment.Left;
        _newName.PlaceholderText = Loc.Get("GamesNewNameHint");
        OnEnterEscape(_newName, AddProfile, HideAddPanel);
        _addPanel.Children.Add(_newName);
        _addPanel.Children.Add(Buttons(
            IconButton(Loc.Get("ProfileAdd"), null, "btn primary sm", AddProfile),
            IconButton(Loc.Get("GamesCancel"), null, "btn ghost sm", HideAddPanel)));
        _addPanel.Children.Add(_addError);
        var listColumn = new StackPanel { Spacing = 8 };
        listColumn.Children.Add(new TextBlock { Classes = { "sub", "lbl" }, Text = Loc.Get("GamesMyGames"), Margin = new Thickness(4, 0) });
        listColumn.Children.Add(_gameList);
        listColumn.Children.Add(_addGame);
        listColumn.Children.Add(_addPanel);
        _listColumn = listColumn;

        // ---- whole game section
        _rename = IconButton(Loc.Get("ProfileRename"), PencilGlyph, "btn sm", ShowRenamePanel);
        _delete = IconButton(Loc.Get("GamesDelete"), TrashGlyph, "btn sm ghost", DeleteProfile, badText: true);
        _renameBox.PlaceholderText = Loc.Get("GamesListNameHint");
        OnEnterEscape(_renameBox, RenameProfile, HideRenamePanel);
        var renameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        renameRow.Children.Add(_renameBox);
        var renameButtons = Buttons(
            IconButton(Loc.Get("GamesSave"), null, "btn primary sm", RenameProfile),
            IconButton(Loc.Get("GamesCancel"), null, "btn ghost sm", HideRenamePanel));
        Grid.SetColumn(renameButtons, 1);
        renameRow.Children.Add(renameButtons);
        _renamePanel.Children.Add(renameRow);
        _renamePanel.Children.Add(_renameError);

        CommitOn(_gameTitle, CommitGameTitle);
        _sourceLang.ItemsSource = SourceLanguages.All.Select(l => l.ThaiName).ToList();
        _sourceLang.SelectionChanged += (_, _) => EditActive(p =>
        {
            if (_sourceLang.SelectedIndex is >= 0 and var i && i < SourceLanguages.All.Count)
                p.SourceLang = SourceLanguages.All[i].Code;
        }, after: () =>
        {
            _ocrMessage.IsVisible = false;
            _ = RefreshOcrAsync();
            UpdateLibraryNote();
        });

        _ocrInstall = IconButton("", null, "btn primary sm", () => _ = InstallOcrAsync());
        var ocrRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        ocrRow.Children.Add(new StrokeIcon { Data = WarnGlyph, Size = 18, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_ocrNoteText, 1);
        ocrRow.Children.Add(_ocrNoteText);
        _ocrInstall.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_ocrInstall, 2);
        ocrRow.Children.Add(_ocrInstall);
        _ocrNote = new Border { Classes = { "note", "warn" }, Child = ocrRow, IsVisible = false };

        _library.SelectionChanged += (_, _) => Safe(CommitLibraryGame);
        _openLibrary = IconButton(null, FolderGlyph, "btn", OpenLibraryFolderInExplorer);
        _openLibrary.Padding = new Thickness(11, 0);
        ToolTip.SetTip(_openLibrary, Loc.Get("OpenLibraryFolder"));
        Avalonia.Automation.AutomationProperties.SetName(_openLibrary, Loc.Get("OpenLibraryFolder"));
        var libraryRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        libraryRow.Children.Add(_library);
        Grid.SetColumn(_openLibrary, 1);
        libraryRow.Children.Add(_openLibrary);

        var wholeHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        wholeHeader.Children.Add(SectionHeader(Loc.Get("GamesWhole"), Loc.Get("GamesWholeSub")));
        var headerButtons = Buttons(_rename, _delete);
        headerButtons.Spacing = 6;
        headerButtons.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(headerButtons, 1);
        wholeHeader.Children.Add(headerButtons);

        var langBlock = new StackPanel { Spacing = 10 };
        langBlock.Children.Add(FormRow(Loc.Get("GamesLangLabel"), Loc.Get("GamesLangSub"), _sourceLang));
        langBlock.Children.Add(_ocrNote);
        langBlock.Children.Add(_ocrMessage);

        var minBlock = new StackPanel { Spacing = 10 };
        minBlock.Children.Add(FormLabel(Loc.Get("GamesMinLabel"), Loc.Get("GamesMinSub")));
        minBlock.Children.Add(new Border { Classes = { "seg" }, Child = _minPanel });

        var libraryBlock = new StackPanel { Spacing = 8 };
        libraryBlock.Children.Add(FormRow(Loc.Get("LibraryLabel"), Loc.Get("GamesLibrarySub"), libraryRow));
        libraryBlock.Children.Add(_libraryNote);

        var whole = Card(
            wholeHeader,
            _renamePanel,
            FormRow(Loc.Get("GamesTitleLabel"), Loc.Get("GamesTitleSub"), _gameTitle),
            Hr(),
            langBlock,
            Hr(),
            minBlock,
            Hr(),
            libraryBlock);

        // ---- zones section
        var addZone = IconButton(Loc.Get("GamesAddZone"), PlusGlyph, "btn sm ghost", AddNewZone);
        var zoneBar = new WrapPanel { Orientation = Orientation.Horizontal };
        zoneBar.Children.Add(new Border { Classes = { "seg" }, Child = _zoneTabs, Margin = new Thickness(0, 0, 8, 4) });
        addZone.Margin = new Thickness(0, 0, 0, 4);
        zoneBar.Children.Add(addZone);

        var editBoxes = IconButton(Loc.Get("GamesEditBoxes"), FrameGlyph, "btn", () =>
        {
            if (_zoneId is { } id)
                Host.RequestRegionSelection(id);
        });
        editBoxes.VerticalAlignment = VerticalAlignment.Center;
        var boxRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        boxRow.Children.Add(_boxLines);
        Grid.SetColumn(editBoxes, 1);
        boxRow.Children.Add(editBoxes);
        var boxCard = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Child = boxRow,
        };
        boxCard.Bind(Border.BackgroundProperty, boxCard.GetResourceObservable("Card2Brush"));
        boxCard.Bind(Border.BorderBrushProperty, boxCard.GetResourceObservable("BorderBrush"));

        _deleteZone = IconButton(Loc.Get("GamesDeleteZone"), TrashGlyph, "btn sm ghost", DeleteSelectedZone, badText: true);
        _deleteZone.VerticalAlignment = VerticalAlignment.Center;
        var zoneNameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        zoneNameRow.Children.Add(_zoneName);
        Grid.SetColumn(_deleteZone, 1);
        zoneNameRow.Children.Add(_deleteZone);
        CommitOn(_zoneName, CommitZoneName);

        _filter.ItemsSource = new[] { Loc.Get("FilterNormal"), Loc.Get("GamesFilterWhite"), Loc.Get("FilterDarkText") };
        _filter.SelectionChanged += (_, _) => EditZone(z =>
        {
            if (_filter.SelectedIndex is >= 0 and var i && i < Filters.Length)
                z.Filter = Filters[i];
        });
        _placement.ItemsSource = new[] { Loc.Get("PlacementCustom"), Loc.Get("GamesPlacementFit") };
        _placement.SelectionChanged += (_, _) => EditZone(z =>
        {
            if (_placement.SelectedIndex is >= 0 and var i && i < Placements.Length)
                z.Placement = Placements[i];
        });
        _sendToWeb.IsCheckedChanged += (_, _) => EditZone(z => z.SendToWeb = _sendToWeb.IsChecked == true);
        _learn.IsCheckedChanged += (_, _) => EditZone(z => z.LearnToLibrary = _learn.IsChecked == true);
        _speakerLinesOnly.IsCheckedChanged += (_, _) => EditZone(z => z.SpeakerLinesOnly = _speakerLinesOnly.IsChecked == true);
        _trigger.IsCheckedChanged += (_, _) =>
            EditZone(z => z.TriggerEnabled = _trigger.IsChecked == true, after: ReloadZoneBlock);
        CommitOn(_triggerWords, CommitZoneTriggerWords);

        var speakerWarn = new Border
        {
            Classes = { "note", "warn" },
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = IconText(WarnGlyph, Loc.Get("GamesSpeakerWarn"), 14),
        };

        var zones = Card(
            SectionHeader(Loc.Get("GamesZones"), Loc.Get("GamesZonesSub")),
            zoneBar,
            boxCard,
            FormRow(Loc.Get("ZoneNameLabel"), null, zoneNameRow),
            Hr(),
            FormRow(Loc.Get("GamesFilterLabel"), Loc.Get("GamesFilterSub"), _filter),
            Hr(),
            FormRow(Loc.Get("GamesPlacementLabel"), null, _placement),
            Hr(),
            FormToggleRow(Loc.Get("GamesSendToWeb"), Loc.Get("GamesSendToWebSub"), _sendToWeb),
            FormToggleRow(Loc.Get("GamesLearn"), Loc.Get("GamesLearnSub"), _learn),
            FormToggleRow(Loc.Get("SpeakerLinesOnly"), Loc.Get("GamesSpeakerSub"), _speakerLinesOnly, speakerWarn),
            FormToggleRow(Loc.Get("GamesTrigger"), Loc.Get("GamesTriggerSub"), _trigger, _triggerWords));
        _triggerWords.Margin = new Thickness(0, 8, 0, 0);

        _editor = PageBody(whole, zones);
        _editor.VerticalAlignment = VerticalAlignment.Top;
        _listColumn.VerticalAlignment = VerticalAlignment.Top;

        _layout = new Grid { ColumnSpacing = 20, RowSpacing = 20 };
        _layout.Children.Add(_listColumn);
        _layout.Children.Add(_editor);
        ApplyLayout(narrow: false);

        var body = PageBody(PageHeader(Loc.Get("NavGames"), Loc.Get("GamesSubtitle")), _layout);
        SetBody(body);
        SizeChanged += (_, e) => Safe(() => ApplyLayout(e.NewSize.Width - 64 < NarrowWidth));
    }

    // ---- page hooks -----------------------------------------------------------------------------------------------

    public override void OnShown() => Safe(() =>
    {
        HideAddPanel();
        HideRenamePanel();
        Load();
    });

    /// <summary>Commits the half-typed game title, zone name and trigger words (into the game they were filled for).</summary>
    public override void CommitPending()
    {
        Safe(CommitGameTitle);
        Safe(CommitZoneName);
        Safe(CommitZoneTriggerWords);
    }

    /// <summary>
    /// Like the old window's StateChanged handler: a different active game reloads everything; otherwise the list, the
    /// zone block (the box editor may have changed it), a newly installed library and the OCR install state are synced.
    /// </summary>
    protected override void OnHostStateChangedCore()
    {
        var p = Settings.GetActive();
        if (!ReferenceEquals(p, _loadedProfile))
        {
            HideAddPanel();
            HideRenamePanel();
            Load();
            return;
        }
        bool was = Loading;
        Loading = true;
        try
        {
            LoadProfiles();
            LoadZoneBlock(p, profileChanged: false);
            if (Host.LibraryListVersion != _shownLibraryListVersion)
                LoadLibraryChoices(p); // a library was installed: show its new version
        }
        finally
        {
            Loading = was;
        }
        UpdateLibraryNote(); // the target language may have changed
        // An install started or finished (possibly from Home).
        if (InstallingKey() != _shownInstalling)
            _ = RefreshOcrAsync();
    }

    // ---- loading (old LoadAll/LoadProfiles/LoadEditor) ------------------------------------------------------------

    private void Load()
    {
        var p = Settings.GetActive();
        bool profileChanged = !ReferenceEquals(p, _loadedProfile);
        bool was = Loading;
        Loading = true;
        try
        {
            _loadedProfile = p;
            LoadProfiles();
            LoadEditor(p);
            LoadZoneBlock(p, profileChanged);
        }
        finally
        {
            Loading = was;
        }
        _ocrMessage.IsVisible = false;
        _ = RefreshOcrAsync();
    }

    /// <summary>Call with <see cref="SettingsPage.Loading"/> set: rebuilds the game list when the names or the active game changed.</summary>
    private void LoadProfiles()
    {
        var active = Settings.GetActive();
        string key = string.Join("\n", Settings.Profiles.Select(p => p.Name)) + "\n\n" + active.Name;
        _delete.IsEnabled = Settings.Profiles.Count > 1;
        if (key == _shownProfiles)
            return;
        _shownProfiles = key;
        _gameList.Children.Clear();
        foreach (var profile in Settings.Profiles)
            _gameList.Children.Add(GameItem(profile.Name, profile == active));
    }

    private RadioButton GameItem(string name, bool active)
    {
        var icon = new StrokeIcon { Data = GamepadGlyph, Size = 20, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(StrokeIcon.ForegroundProperty, icon.GetResourceObservable("AccentBrush"));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Classes = { "lbl" }, Text = name, TextWrapping = TextWrapping.Wrap });
        var tags = new List<string>();
        if (active)
            tags.Add(Loc.Get("GamesActive"));
        if (GameProfile.IsDefaultName(name))
            tags.Add(Loc.Get("GamesDefault"));
        if (tags.Count > 0)
            text.Children.Add(new TextBlock { Classes = { "sub" }, Text = string.Join(" · ", tags) });
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        row.Children.Add(icon);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var item = new RadioButton { Classes = { "opt" }, Content = row, IsChecked = active };
        item.IsCheckedChanged += (_, _) =>
        {
            if (Loading || item.IsChecked != true)
                return;
            // Posted: switching rebuilds this list, which must not happen inside the item's own event.
            Dispatcher.UIThread.Post(() => Safe(() => PickProfile(name)));
        };
        return item;
    }

    /// <summary>Old ProfileList.SelectionChanged: switches through AppHost (raises ActiveProfileChanging first).</summary>
    private void PickProfile(string name)
    {
        if (Settings.GetActive().Name != name)
            Host.SelectProfile(name);
        if (ReferenceEquals(Settings.GetActive(), _loadedProfile))
        {
            // Nothing switched (the game is gone or already active): put the list back.
            _shownProfiles = null;
            bool was = Loading;
            Loading = true;
            try
            {
                LoadProfiles();
            }
            finally
            {
                Loading = was;
            }
        }
        else
        {
            Load();
        }
    }

    /// <summary>Call with <see cref="SettingsPage.Loading"/> set: fills the whole-game block without writing back.</summary>
    private void LoadEditor(GameProfile p)
    {
        int lang = SourceLanguages.All.ToList().FindIndex(l => l.Code == p.SourceLang);
        _sourceLang.SelectedIndex = Math.Max(lang, 0);
        _gameTitle.Text = p.GameTitle ?? "";
        LoadMinDisplay(p);
        LoadLibraryChoices(p);
    }

    // ---- minimum display time (New: segmented control) ------------------------------------------------------------

    /// <summary>
    /// Call with <see cref="SettingsPage.Loading"/> set: the fixed choices plus the game's own value when it is none of
    /// them ("3 วิ (ค่าเดิม)"); rebuilt only when that set changes.
    /// </summary>
    private void LoadMinDisplay(GameProfile p)
    {
        double current = Math.Clamp(p.MinDisplaySeconds, 0, GameProfile.MinDisplayMax);
        double[] choices = [.. MinDisplayChoices.For(current)];
        if (!choices.SequenceEqual(_minChoices))
        {
            _minChoices = choices;
            _minPanel.Children.Clear();
            foreach (double value in choices)
                _minPanel.Children.Add(MinDisplayButton(value));
        }
        foreach (var button in _minPanel.Children.OfType<RadioButton>())
            button.IsChecked = button.Tag is double v && v == current;
    }

    private RadioButton MinDisplayButton(double value)
    {
        string label = value == 0
            ? Loc.Get("GamesMinFollow")
            : MinDisplayChoices.Values.Contains(value)
                ? Loc.Format("GamesMinSeconds", value.ToString("0.#", CultureInfo.InvariantCulture))
                : Loc.Format("GamesMinLegacy", value.ToString("0.##", CultureInfo.InvariantCulture));
        var button = new RadioButton { Classes = { "seg" }, Content = label, Tag = value, Margin = new Thickness(0, 0, 4, 0) };
        button.IsCheckedChanged += (_, _) =>
        {
            if (Loading || button.IsChecked != true)
                return;
            EditActive(p => p.MinDisplaySeconds = Math.Clamp(value, 0, GameProfile.MinDisplayMax),
                // Posted: the old value's extra choice goes away, which rebuilds the buttons (not inside this event).
                after: () => Dispatcher.UIThread.Post(() => Safe(ReloadMinDisplay)));
        };
        return button;
    }

    private void ReloadMinDisplay()
    {
        bool was = Loading;
        Loading = true;
        try
        {
            LoadMinDisplay(Settings.GetActive());
        }
        finally
        {
            Loading = was;
        }
    }

    // ---- game title, library (ported) -----------------------------------------------------------------------------

    /// <summary>LostFocus/Enter on the game title: blank clears it. Only into the game the box was filled for.</summary>
    private void CommitGameTitle()
    {
        if (Loading || _loadedProfile is null || !ReferenceEquals(Settings.GetActive(), _loadedProfile))
            return;
        string title = (_gameTitle.Text ?? "").Trim();
        string? value = title.Length == 0 ? null : title;
        if (_gameTitle.Text != title)
            _gameTitle.Text = title;
        if (Settings.GetActive().GameTitle == value)
            return;
        EditActive(p => p.GameTitle = value);
    }

    /// <summary>
    /// Call with <see cref="SettingsPage.Loading"/> set: lists the libraries found (bundled and downloaded) and selects
    /// the game's. A library the game names but that is missing stays listed by its folder name.
    /// </summary>
    private void LoadLibraryChoices(GameProfile p)
    {
        _shownLibraryListVersion = Host.LibraryListVersion;
        var games = new List<string?> { null };
        var items = new List<string> { Loc.Get("LibraryNone") };
        foreach (var listing in Host.LibraryStore.List())
        {
            games.Add(listing.Game);
            items.Add($"{listing.Title ?? listing.Game} ({listing.Version})");
        }
        int index = 0;
        if (!string.IsNullOrWhiteSpace(p.LibraryGame))
        {
            index = games.FindIndex(g => string.Equals(g, p.LibraryGame.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                games.Add(p.LibraryGame);
                items.Add(p.LibraryGame);
                index = games.Count - 1;
            }
        }
        _libraryGames = [.. games];
        _library.ItemsSource = items;
        _library.SelectedIndex = index;
        UpdateLibraryNote();
    }

    /// <summary>The library drop-down: the first item (ไม่ใช้คลัง) is null.</summary>
    private void CommitLibraryGame()
    {
        if (Loading)
            return;
        if (_library.SelectedIndex is not (>= 0 and var i) || i >= _libraryGames.Length)
            return;
        string? game = _libraryGames[i];
        if (Settings.GetActive().LibraryGame == game)
            return;
        EditActive(p => p.LibraryGame = game, after: UpdateLibraryNote);
    }

    /// <summary>The note under the library: why it cannot apply (not English → Thai). Hidden without a library or when it applies.</summary>
    private void UpdateLibraryNote()
    {
        string? game = Settings.GetActive().LibraryGame;
        string text = !string.IsNullOrWhiteSpace(game) && !Host.LibraryLanguagesApply ? Loc.Get("LibraryNeedsEnTh") : "";
        if (_libraryNote.Text != text)
            _libraryNote.Text = text;
        _libraryNote.IsVisible = text.Length > 0;
        _openLibrary.IsEnabled = AppHost.IsLibraryName(game);
    }

    /// <summary>Opens the game's folder of downloaded libraries in the file manager, creating it first.</summary>
    private void OpenLibraryFolderInExplorer()
    {
        string? game = Settings.GetActive().LibraryGame;
        if (!AppHost.IsLibraryName(game))
            return;
        string dir = Host.LibraryStore.UserDir(game!.Trim());
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true })?.Dispose();
    }

    // ---- add / rename / delete (old AddProfile/RenameProfile/DeleteProfile, through AppHost) ------------------------

    private void ShowAddPanel()
    {
        _newName.Text = "";
        _addError.IsVisible = false;
        _addPanel.IsVisible = true;
        _addGame.IsVisible = false;
        _newName.Focus();
    }

    private void HideAddPanel()
    {
        _addPanel.IsVisible = false;
        _addError.IsVisible = false;
        _addGame.IsVisible = true;
    }

    private void AddProfile()
    {
        string name = (_newName.Text ?? "").Trim();
        if (!ValidateProfileName(name, except: null, _addError))
            return;
        if (Host.AddProfile(name)) // commits this page into the game being left, then switches
        {
            HideAddPanel();
            Load();
        }
    }

    private void ShowRenamePanel()
    {
        _renameBox.Text = Settings.GetActive().Name;
        _renameError.IsVisible = false;
        _renamePanel.IsVisible = true;
        _renameBox.Focus();
        _renameBox.SelectAll();
    }

    private void HideRenamePanel()
    {
        _renamePanel.IsVisible = false;
        _renameError.IsVisible = false;
    }

    private void RenameProfile()
    {
        string name = (_renameBox.Text ?? "").Trim();
        var active = Settings.GetActive();
        if (name == active.Name)
        {
            HideRenamePanel();
            return;
        }
        if (!ValidateProfileName(name, except: active, _renameError))
            return;
        if (Host.RenameActiveProfile(name))
        {
            HideRenamePanel();
            _shownProfiles = null;
            Load();
        }
    }

    private void DeleteProfile()
    {
        if (Settings.Profiles.Count <= 1)
            return;
        HideRenamePanel();
        if (Host.DeleteActiveProfile())
            Load();
    }

    private bool ValidateProfileName(string name, GameProfile? except, TextBlock errorText)
    {
        string? error = null;
        if (name.Length == 0)
            error = Loc.Get("GamesNameEmpty");
        else if (Settings.Profiles.Any(p => p != except && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            error = Loc.Get("GamesNameTaken");
        errorText.Text = error;
        errorText.IsVisible = error is not null;
        return error is null;
    }

    // ---- zones (ported) -------------------------------------------------------------------------------------------

    /// <summary>
    /// Call with <see cref="SettingsPage.Loading"/> set: fills the zone block from the selected zone (the game's last one
    /// when the game changed or the selection is gone). Text boxes being typed in are left alone.
    /// </summary>
    private void LoadZoneBlock(GameProfile p, bool profileChanged)
    {
        var zone = (profileChanged ? null : p.GetZone(_zoneId)) ?? p.GetLastZone();
        bool sameZone = ReferenceEquals(_textsProfile, p) && _textsZoneId == zone.Id;
        _zoneId = zone.Id;

        string key = string.Join("\n", p.Zones.Select(z => z.Id + z.Name));
        if (key != _shownZones)
        {
            _shownZones = key;
            var tabs = _zoneTabs.Children.OfType<RadioButton>().ToList();
            if (tabs.Select(t => t.Tag as string).SequenceEqual(p.Zones.Select(z => z.Id)))
            {
                // Only names changed: relabel in place (a rebuild under the pointer could swallow a click).
                for (int i = 0; i < tabs.Count; i++)
                    tabs[i].Content = p.Zones[i].Name;
            }
            else
            {
                _zoneTabs.Children.Clear();
                foreach (var z in p.Zones)
                    _zoneTabs.Children.Add(ZoneTab(z.Id, z.Name));
            }
        }
        foreach (var tab in _zoneTabs.Children.OfType<RadioButton>())
            tab.IsChecked = tab.Tag as string == zone.Id;
        _deleteZone.IsEnabled = p.Zones.Count > 1;

        if (!(sameZone && _zoneName.IsFocused))
            _zoneName.Text = zone.Name;
        _placement.SelectedIndex = Math.Max(Array.IndexOf(Placements, zone.Placement), 0);
        _filter.SelectedIndex = Math.Max(Array.IndexOf(Filters, zone.Filter), 0);
        _sendToWeb.IsChecked = zone.SendToWeb;
        _learn.IsChecked = zone.LearnToLibrary;
        _speakerLinesOnly.IsChecked = zone.SpeakerLinesOnly;
        _trigger.IsChecked = zone.TriggerEnabled;
        if (!(sameZone && _triggerWords.IsFocused))
            _triggerWords.Text = zone.TriggerWords;
        _triggerWords.IsVisible = zone.TriggerEnabled;
        _textsProfile = p;
        _textsZoneId = zone.Id;
        ShowBoxes(zone);
    }

    private RadioButton ZoneTab(string id, string name)
    {
        var tab = new RadioButton { Classes = { "seg" }, Content = name, Tag = id, Margin = new Thickness(0, 0, 4, 0) };
        tab.IsCheckedChanged += (_, _) =>
        {
            // Posted: committing the zone being left may refill the tabs, which must not happen inside this event.
            if (!Loading && tab.IsChecked == true)
                Dispatcher.UIThread.Post(() => Safe(() => PickZone(id)));
        };
        return tab;
    }

    /// <summary>Which boxes of the zone are set (old ZoneBoxesState and ZoneTriggerNoBox).</summary>
    private void ShowBoxes(OcrZone zone)
    {
        bool read = zone.Region is not null, caption = zone.GetCaptionRegion() is not null, trigger = zone.TriggerRegion is not null;
        string key = $"{read}{caption}{zone.TriggerEnabled}{trigger}{ActualThemeVariant}";
        if (key == _shownBoxes)
            return;
        _shownBoxes = key;
        _boxLines.Children.Clear();
        _boxLines.Children.Add(BoxLine(Loc.Get("GamesBoxRead"), read));
        _boxLines.Children.Add(BoxLine(Loc.Get("GamesBoxCaption"), caption));
        if (zone.TriggerEnabled)
            _boxLines.Children.Add(BoxLine(Loc.Get("GamesBoxTrigger"), trigger));
    }

    private static Control BoxLine(string what, bool set)
    {
        var icon = new StrokeIcon { Data = set ? CheckCircleGlyph : WarnGlyph, Size = 18, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(StrokeIcon.ForegroundProperty, icon.GetResourceObservable(set ? "OkBrush" : "WarnBrush"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(icon);
        row.Children.Add(new TextBlock
        {
            Text = Loc.Format(set ? "GamesBoxSet" : "GamesBoxNotSet", what),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        return row;
    }

    /// <summary>Refills the zone block after an edit that changes what it shows (name, trigger, list).</summary>
    private void ReloadZoneBlock()
    {
        bool was = Loading;
        Loading = true;
        try
        {
            LoadZoneBlock(Settings.GetActive(), profileChanged: false);
        }
        finally
        {
            Loading = was;
        }
    }

    /// <summary>Edits the selected zone like <see cref="SettingsPage.EditActive"/> edits the game.</summary>
    private void EditZone(Action<OcrZone> edit, Action? after = null) => EditActive(p =>
    {
        if (p.GetZone(_zoneId) is { } zone)
            edit(zone);
    }, after);

    private void PickZone(string id)
    {
        if (Loading || id == _zoneId || Settings.GetActive().GetZone(id) is null)
            return;
        // The boxes being typed in belong to the zone being left.
        CommitZoneName();
        CommitZoneTriggerWords();
        _zoneId = id;
        ReloadZoneBlock();
    }

    private void AddNewZone() => EditActive(p =>
    {
        _zoneId = p.AddZone().Id;
    }, after: ReloadZoneBlock);

    private void DeleteSelectedZone() => EditActive(p =>
    {
        if (p.Zones.Count <= 1 || p.GetZone(_zoneId) is not { } zone)
            return;
        int index = p.Zones.IndexOf(zone);
        p.Zones.RemoveAt(index);
        if (p.LastZoneId == zone.Id)
            p.LastZoneId = null;
        _zoneId = p.Zones[Math.Min(index, p.Zones.Count - 1)].Id;
    }, after: ReloadZoneBlock);

    /// <summary>The zone the name and trigger-words boxes were filled for, or null when it is gone or not the selected one.</summary>
    private OcrZone? TextsZone()
    {
        if (Loading)
            return null;
        var p = Settings.GetActive();
        if (!ReferenceEquals(p, _textsProfile) || _textsZoneId != _zoneId)
            return null;
        return p.GetZone(_textsZoneId);
    }

    /// <summary>LostFocus/Enter on the zone name: blank reverts.</summary>
    private void CommitZoneName()
    {
        if (TextsZone() is not { } zone)
            return;
        string name = (_zoneName.Text ?? "").Trim();
        if (name.Length == 0)
            name = zone.Name;
        if (_zoneName.Text != name)
            _zoneName.Text = name;
        if (zone.Name != name)
            EditZone(z => z.Name = name, after: ReloadZoneBlock);
    }

    /// <summary>LostFocus/Enter on the trigger words: saved as typed.</summary>
    private void CommitZoneTriggerWords()
    {
        if (TextsZone() is not { } zone)
            return;
        string words = _triggerWords.Text ?? "";
        if (zone.TriggerWords != words)
            EditZone(z => z.TriggerWords = words);
    }

    // ---- OCR language of the game (old OCR tab: RefreshOcrRowsAsync / InstallOcrAsync, for one language) ----------

    private SourceLanguage? CurrentLanguage()
    {
        string code = Settings.GetActive().SourceLang;
        return SourceLanguages.All.FirstOrDefault(l => l.Code == code);
    }

    private string InstallingKey() =>
        string.Join(",", SourceLanguages.All.Where(Host.IsInstallingOcrLanguage).Select(l => l.Code));

    /// <summary>Shows the install note under "ภาษาในเกม" only when the game's language is missing (or installing).</summary>
    private async Task RefreshOcrAsync()
    {
        try
        {
            int seq = ++_ocrSeq;
            _shownInstalling = InstallingKey();
            if (CurrentLanguage() is not { } language)
            {
                _ocrNote.IsVisible = false;
                return;
            }
            if (Host.IsInstallingOcrLanguage(language))
            {
                ShowOcrInstalling(language);
                return;
            }
            bool available = await Host.IsOcrLanguageAvailableAsync(language);
            if (seq != _ocrSeq || CurrentLanguage() != language)
                return;
            if (Host.IsInstallingOcrLanguage(language))
                ShowOcrInstalling(language);
            else
                ShowOcrAvailable(language, available);
        }
        catch (Exception)
        {
            // The note keeps its last state.
        }
    }

    private async Task InstallOcrAsync()
    {
        try
        {
            if (CurrentLanguage() is not { } language || Host.IsInstallingOcrLanguage(language))
                return;
            _ocrMessage.IsVisible = false;
            ShowOcrInstalling(language);
            bool ok = await Host.InstallOcrLanguageAsync(language);
            bool available = await Host.IsOcrLanguageAvailableAsync(language);
            if (CurrentLanguage() != language)
                return; // the game's language changed meanwhile: that language's state is already shown
            ++_ocrSeq;
            ShowOcrAvailable(language, available);
            _ocrMessage.Text = Loc.Get(!ok ? "OcrInstallFailed" : available ? "OcrInstallDone" : "OcrInstallNeedsRestart");
            _ocrMessage.IsVisible = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Settings: OCR install failed", ex); // the note stays as it was
        }
    }

    private void ShowOcrInstalling(SourceLanguage language)
    {
        _ocrNoteText.Text = OcrNoteText(language);
        _ocrInstall.Content = Loc.Get("IssueOcrInstalling");
        _ocrInstall.IsEnabled = false;
        _ocrNote.IsVisible = true;
    }

    private void ShowOcrAvailable(SourceLanguage language, bool available)
    {
        _ocrNote.IsVisible = !available;
        if (available)
            return;
        _ocrNoteText.Text = OcrNoteText(language);
        _ocrInstall.Content = Loc.Format("IssueOcrButton", language.ThaiName);
        _ocrInstall.IsEnabled = true;
    }

    /// <summary>"ยังไม่มีภาษา…สำหรับอ่านตัวอักษร", plus the "answer Yes to Windows" note on Windows only.</summary>
    private string OcrNoteText(SourceLanguage language)
    {
        string text = Loc.Format("IssueOcrTitle", language.ThaiName);
        if (Host.Platform.Kind == PlatformKind.Windows)
            text += Loc.Get("IssueOcrWinNote");
        return text;
    }

    // ---- layout helpers -------------------------------------------------------------------------------------------

    private void ApplyLayout(bool narrow)
    {
        if (_narrow == narrow)
            return;
        _narrow = narrow;
        if (narrow)
        {
            _layout.ColumnDefinitions = new ColumnDefinitions("*");
            _layout.RowDefinitions = new RowDefinitions("Auto,Auto");
            Grid.SetColumn(_listColumn, 0);
            Grid.SetRow(_listColumn, 0);
            Grid.SetColumn(_editor, 0);
            Grid.SetRow(_editor, 1);
        }
        else
        {
            _layout.ColumnDefinitions = new ColumnDefinitions("220,*");
            _layout.RowDefinitions = new RowDefinitions("Auto");
            Grid.SetColumn(_listColumn, 0);
            Grid.SetRow(_listColumn, 0);
            Grid.SetColumn(_editor, 1);
            Grid.SetRow(_editor, 0);
        }
    }

    /// <summary>A section card (Border.section) whose children are spaced 16 px apart.</summary>
    private static Border Card(params Control[] children)
    {
        var stack = new StackPanel { Spacing = 16 };
        foreach (var child in children)
            stack.Children.Add(child);
        return new Border { Classes = { "section" }, Child = stack };
    }

    private static Border Hr() => new() { Classes = { "hr" } };

    private static StackPanel Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var b in buttons)
            row.Children.Add(b);
        return row;
    }

    private static StackPanel IconText(string glyph, string text, double size)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        row.Children.Add(new StrokeIcon { Data = glyph, Size = size, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(block, 1);
        row.Children.Add(block);
        return new StackPanel { Children = { row } };
    }

    /// <summary>A styled button with an optional icon; <paramref name="badText"/> draws its content in the "bad" colour.</summary>
    private static Button IconButton(string? text, string? glyph, string classes, Action action, bool badText = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        StrokeIcon? icon = null;
        TextBlock? label = null;
        if (glyph is not null)
        {
            icon = new StrokeIcon { Data = glyph, Size = 16, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(icon);
        }
        if (!string.IsNullOrEmpty(text))
        {
            label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(label);
        }
        var button = new Button { Content = glyph is null ? text : row };
        button.Classes.AddRange(classes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        button.Click += (_, _) => Safe(action);
        if (badText)
        {
            icon?.Bind(StrokeIcon.ForegroundProperty, button.GetResourceObservable("BadBrush"));
            label?.Bind(TextBlock.ForegroundProperty, button.GetResourceObservable("BadBrush"));
        }
        return button;
    }

    private static TextBlock ErrorText()
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false, FontSize = 13 };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable("BadBrush"));
        return block;
    }

    private static void OnEnterEscape(TextBox box, Action enter, Action escape) =>
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Safe(enter);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Safe(escape);
            }
        };
}
