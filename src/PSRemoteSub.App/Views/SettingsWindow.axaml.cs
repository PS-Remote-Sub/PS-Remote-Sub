using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Input;
using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Layout;
using PSRemoteSub.Core.Net;
using PSRemoteSub.Core.Pipeline;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;
using PSRemoteSub.Core.Translation.Ai;
using QRCoder;

namespace PSRemoteSub.App.Views;

/// <summary>
/// Settings in seven Thai tabs (the picture source tab is its own <see cref="PictureSourcePanel"/>; the TV tab is in
/// <c>SettingsWindow.Tv.cs</c>). Every change is applied at once (<see cref="AppHost.ApplySettings"/>) and saved
/// (debounced for sliders and text); translation changes go through <see cref="AppHost.ApplyTranslationSettings"/>.
/// UI thread only; <see cref="AppHost.StateChanged"/> arrives on the UI thread, and blocking, WinRT or network work
/// (firewall rule, OCR languages, network adapters, AI test, Ollama model list) runs on the thread pool.
/// Every event handler catches all exceptions.
/// </summary>
public partial class SettingsWindow : Window
{
    private const double MinFontSize = 16;
    private const double MaxFontSize = 64;
    private const int MinPort = 1024;
    private const int MaxPort = 65535;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);

    private static readonly HotkeyAction[] HotkeyOrder =
        [HotkeyAction.SelectRegion, HotkeyAction.ToggleOverlay, HotkeyAction.TogglePause, HotkeyAction.SaveFrame,
         HotkeyAction.ToggleRegionFrames];
    private static readonly PreprocessFilter[] Filters =
        [PreprocessFilter.Normal, PreprocessFilter.WhiteText, PreprocessFilter.DarkText];
    private static readonly OverlayPlacement[] Placements = [OverlayPlacement.Custom, OverlayPlacement.Fit];
    private static readonly AiProvider[] AiProviders =
        [AiProvider.Claude, AiProvider.OpenAi, AiProvider.Gemini, AiProvider.Ollama, AiProvider.Custom];

    /// <summary>Shown as the key box's placeholder when a key is stored; the stored key itself is never shown.</summary>
    private const string StoredKeyMask = "●●●●●●●●";
    private const string AiTestSentence = "Where is the key?";
    private const int MaxTestTextLength = 200;
    private static readonly string[] AiCopyKeys =
        [AiRefiner.AiDownKey, AiRefiner.AiRateLimitedKey, AiRefiner.AiAuthKey, AiRefiner.AiModelNotFoundKey,
            AiRefiner.AiBillingKey, "AiKeyNeeded", "AiNoModel", AiRefiner.GoogleDownKey];

    /// <summary>Language codes: <c>th</c>, <c>en</c>, <c>zh-CN</c>, <c>zh-TW</c>, <c>haw</c>…</summary>
    private static readonly Regex LanguageCode = new("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,4})?$", RegexOptions.CultureInvariant);

    private readonly AppHost? _host;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly Dictionary<HotkeyAction, (TextBox Box, TextBlock Error)> _hotkeyRows = [];
    private readonly List<OcrRow> _ocrRows = [];
    private List<string> _fonts = [];
    private string[] _shownProfiles = [];
    private string? _shownActive;
    private string? _zoneId;
    private string _shownZones = "";
    // The profile and zone the zone-name and trigger-words boxes were filled for; a commit for any other is dropped.
    private GameProfile? _textsProfile;
    private string? _textsZoneId;
    private string _shownInstalling = "";
    // The library dropdown's games by index: index 0 (ไม่ใช้คลัง) is null.
    private string?[] _libraryGames = [null];
    private int _shownLibraryListVersion = -1;
    private bool _loading;
    private bool _webBusy;
    private bool _closed;
    private PlatformPermission[]? _shownMissingPermissions; // what the permission banner shows (null: not built yet)
    private int _firewallSeq;
    private int _lanSeq;
    private int _statusOcrSeq;
    private int _aiTestSeq;
    private int _aiListSeq;
    // Cancelled when the window closes: stops an AI test or model list still in flight.
    private readonly CancellationTokenSource _closeCts = new();
    private Bitmap? _qr;

    /// <summary>For the XAML loader and designer only; the app uses <see cref="SettingsWindow(AppHost)"/>.</summary>
    public SettingsWindow()
    {
        InitializeComponent();
        _saveTimer = new DispatcherTimer { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) => Safe(FlushSave);
        _statusTimer = new DispatcherTimer { Interval = StatusInterval };
        _statusTimer.Tick += (_, _) => Safe(RefreshStatus);
    }

    public SettingsWindow(AppHost host) : this()
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        SourcePanel.Attach(host);
        BuildStaticContent();
        WireEvents();
        WireTvEvents();
        LoadAll();
        Opened += (_, _) => Safe(OnOpened);
        Activated += (_, _) => Safe(Host.RefreshPermissions); // the user may have just granted one in System Settings
        // A focused text box never sees LostFocus otherwise.
        Closing += (_, _) =>
        {
            Safe(CommitHotkeys);
            Safe(CommitAiFields);
            Safe(CommitGameTitle);
        };
        Closed += (_, _) => Safe(OnClosed);
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
            var open = new Button { Content = Loc.Get("PermOpenSettings"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var permission = p;
            open.Click += (_, _) => Safe(() => Host.OpenPermissionSettings(permission));
            var text = new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = AppHost.PermissionText(p), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Classes = { "hint" }, Text = Loc.Get("PermAfterUpdate") });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
            row.Children.Add(text);
            Grid.SetColumn(open, 1);
            row.Children.Add(open);
            PermBanner.Children.Add(new Border
            {
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#33FFB74D")),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8),
                Child = row,
            });
        }
    }

    private AppHost Host => _host ?? throw new InvalidOperationException("SettingsWindow needs an AppHost.");

    private AppSettings Settings => Host.Settings;

    // ---- lifetime -----------------------------------------------------------------------------------------------

    private void OnOpened()
    {
        Host.StateChanged += OnStateChanged;
        Host.TvFeed.Changed += OnTvFeedChanged;
        LoadTvState(); // a TV may have connected between construction and now
        Host.RefreshPermissions();
        ShowPermissionBanner();
        _statusTimer.Start();
        _ = RefreshOcrRowsAsync();
        _ = RefreshStatusOcrAsync();
        _ = RefreshLanAsync();
        _ = CheckFirewallAsync();
    }

    private void OnClosed()
    {
        _closed = true;
        _closeCts.Cancel();
        Host.StateChanged -= OnStateChanged;
        Host.TvFeed.Changed -= OnTvFeedChanged;
        Host.SetTvCalibrating(false); // the calibration pattern never outlives this window
        SourcePanel.Detach();
        _statusTimer.Stop();
        FlushSave();
        QrImage.Source = null;
        _qr?.Dispose();
        _qr = null;
    }

    private void OnStateChanged(object? sender, EventArgs e) => Safe(() =>
    {
        if (_closed)
            return;
        ShowPermissionBanner();
        LoadProfiles(force: false);
        LoadWebState();
        LoadTvState();
        _loading = true;
        try
        {
            KeepAwake.IsChecked = Settings.KeepAwake;
            ShowRegionFrames.IsChecked = Settings.ShowRegionFrames;
            if (Host.LibraryListVersion != _shownLibraryListVersion)
                LoadLibraryChoices(Settings.GetActive()); // a library was installed: show its new version
        }
        finally
        {
            _loading = false;
        }
        RefreshStatus();
        _ = RefreshStatusOcrAsync();

        // An install started or finished (possibly started before this window was reopened).
        var installing = string.Join(",", SourceLanguages.All.Where(Host.IsInstallingOcrLanguage).Select(l => l.Code));
        if (installing != _shownInstalling)
            _ = RefreshOcrRowsAsync();
    });

    // ---- construction -------------------------------------------------------------------------------------------

    private void BuildStaticContent()
    {
        SourceLang.ItemsSource = SourceLanguages.All.Select(l => l.ThaiName).ToList();
        Filter.ItemsSource = new[] { "FilterNormal", "FilterWhiteText", "FilterDarkText" }.Select(Loc.Get).ToList();
        Placement.ItemsSource = new[] { "PlacementCustom", "PlacementFit" }.Select(Loc.Get).ToList();
        try
        {
            _fonts = [.. FontManager.Current.SystemFonts.Select(f => f.Name).Distinct()
                .Order(StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception)
        {
            _fonts = [];
        }
        FontFamilyBox.ItemsSource = _fonts;

        for (int i = 0; i < HotkeyOrder.Length; i++)
        {
            var action = HotkeyOrder[i];
            var label = new TextBlock
            {
                Text = Loc.Get("Hotkey" + action),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 16, 4),
            };
            var box = new TextBox { Name = "Hotkey" + action, Width = 170, MaxLength = 24, Margin = new Thickness(0, 4) };
            var error = new TextBlock { IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            error.Classes.Add("error");
            Grid.SetRow(label, i);
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            Grid.SetRow(error, i);
            Grid.SetColumn(error, 2);
            HotkeyGrid.Children.Add(label);
            HotkeyGrid.Children.Add(box);
            HotkeyGrid.Children.Add(error);
            box.LostFocus += (_, _) => Safe(CommitHotkeys);
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    Safe(CommitHotkeys);
                }
            };
            _hotkeyRows[action] = (box, error);
        }

        foreach (var language in SourceLanguages.All)
        {
            var row = new OcrRow(language);
            row.Install.Click += (_, _) => _ = InstallOcrAsync(row);
            OcrRows.Children.Add(row.Root);
            _ocrRows.Add(row);
        }

        VersionText.Text = Loc.Format("VersionLabel", AppHost.Version);

        AiProviderBox.ItemsSource = AiProviders.Select(ProviderDisplayName).ToList();
    }

    private static string ProviderDisplayName(AiProvider p) => p switch
    {
        AiProvider.Claude => "Claude",
        AiProvider.OpenAi => "OpenAI",
        AiProvider.Gemini => "Gemini",
        AiProvider.Ollama => "Ollama",
        _ => Loc.Get("AiProviderCustom"),
    };

    /// <summary>Runs <paramref name="commit"/> on LostFocus and on Enter.</summary>
    private static void CommitOnLeave(TextBox box, Action commit)
    {
        box.LostFocus += (_, _) => Safe(commit);
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            Safe(commit);
        };
    }

    private void WireEvents()
    {
        ProfileList.SelectionChanged += (_, _) => Safe(() =>
        {
            if (!_loading && ProfileList.SelectedItem is string name)
                Host.SelectProfile(name);
        });
        ProfileAdd.Click += (_, _) => Safe(AddProfile);
        ProfileRename.Click += (_, _) => Safe(RenameProfile);
        ProfileDelete.Click += (_, _) => Safe(DeleteProfile);

        SourceLang.SelectionChanged += (_, _) => EditActive(p =>
        {
            if (SourceLang.SelectedIndex is >= 0 and var i && i < SourceLanguages.All.Count)
                p.SourceLang = SourceLanguages.All[i].Code;
        }, afterApply: () =>
        {
            _ = RefreshStatusOcrAsync();
            UpdateLibraryNote();
        });
        ZonePicker.SelectionChanged += (_, _) => Safe(PickZone);
        AddZone.Click += (_, _) => Safe(AddNewZone);
        DeleteZone.Click += (_, _) => Safe(DeleteSelectedZone);
        CommitOnLeave(ZoneName, CommitZoneName);
        Filter.SelectionChanged += (_, _) => EditZone(z =>
        {
            if (Filter.SelectedIndex is >= 0 and var i && i < Filters.Length)
                z.Filter = Filters[i];
        });
        Placement.SelectionChanged += (_, _) => EditZone(z =>
        {
            if (Placement.SelectedIndex is >= 0 and var i && i < Placements.Length)
                z.Placement = Placements[i];
        });
        ZoneSendToWeb.IsCheckedChanged += (_, _) => EditZone(z => z.SendToWeb = ZoneSendToWeb.IsChecked == true);
        ZoneLearn.IsCheckedChanged += (_, _) => EditZone(z => z.LearnToLibrary = ZoneLearn.IsChecked == true);
        ZoneTrigger.IsCheckedChanged += (_, _) =>
            EditZone(z => z.TriggerEnabled = ZoneTrigger.IsChecked == true, afterApply: ReloadZoneBlock);
        CommitOnLeave(ZoneTriggerWords, CommitZoneTriggerWords);
        EditZoneBoxes.Click += (_, _) => Safe(() =>
        {
            if (_zoneId is { } id)
                Host.RequestRegionSelection(id);
        });
        FontFamilyBox.SelectionChanged += (_, _) => EditActive(p =>
        {
            if (FontFamilyBox.SelectedItem is string font && !string.IsNullOrWhiteSpace(font))
                p.FontFamily = font;
        });
        FontSizeBox.ValueChanged += (_, _) => EditActive(p =>
        {
            if (FontSizeBox.Value is { } v)
                p.FontSize = Math.Clamp((double)v, MinFontSize, MaxFontSize);
        });
        MinDisplayBox.ValueChanged += (_, _) => EditActive(p =>
        {
            if (MinDisplayBox.Value is { } v)
                p.MinDisplaySeconds = Math.Clamp((double)v, 0, GameProfile.MinDisplayMax);
        });
        OpacitySlider.ValueChanged += (_, _) =>
        {
            Safe(() => OpacityText.Text = FormatPercent(OpacitySlider.Value));
            EditActive(p => p.BackgroundOpacity = Math.Round(Math.Clamp(OpacitySlider.Value, 0, 1), 2));
        };
        CommitOnLeave(GameTitle, CommitGameTitle);
        LibraryGame.SelectionChanged += (_, _) => Safe(CommitLibraryGame);
        SpeakerLinesOnly.IsCheckedChanged += (_, _) => EditZone(z => z.SpeakerLinesOnly = SpeakerLinesOnly.IsChecked == true);
        OpenLibraryFolder.Click += (_, _) => Safe(OpenLibraryFolderInExplorer);

        ModeGoogle.IsCheckedChanged += (_, _) => Safe(() => SetTranslationMode(ModeGoogle, TranslationMode.Google));
        ModeAi.IsCheckedChanged += (_, _) => Safe(() => SetTranslationMode(ModeAi, TranslationMode.Ai));
        AiProviderBox.SelectionChanged += (_, _) => Safe(SelectAiProvider);
        CommitOnLeave(AiKey, CommitAiFields);
        CommitOnLeave(AiModel, CommitAiFields);
        CommitOnLeave(AiUrl, CommitAiFields);
        AiKey.TextChanged += (_, _) => Safe(UpdateHttpWarning);
        AiUrl.TextChanged += (_, _) => Safe(UpdateHttpWarning);
        AiContext.ValueChanged += (_, _) => Safe(CommitAiContext);
        AiQueue.ValueChanged += (_, _) => Safe(CommitAiQueue);
        AiParallel.ValueChanged += (_, _) => Safe(CommitAiParallel);
        AiTest.Click += (_, _) => _ = TestAiAsync();
        AiListModels.Click += (_, _) => _ = ListOllamaModelsAsync();
        AiModelList.SelectionChanged += (_, _) => Safe(PickOllamaModel);

        TargetLang.TextChanged += (_, _) => Safe(CommitTargetLanguage);

        WebEnabled.IsCheckedChanged += (_, _) => _ = SetWebEnabledAsync();
        PortApply.Click += (_, _) => _ = ApplyPortAsync();
        Port.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = ApplyPortAsync();
            }
        };
        FirewallButton.Click += (_, _) => _ = OpenFirewallAsync();

        KeepAwake.IsCheckedChanged += (_, _) => Safe(() =>
        {
            if (_loading)
                return;
            Settings.KeepAwake = KeepAwake.IsChecked == true;
            Host.ApplySettings();
            ScheduleSave();
        });

        ShowRegionFrames.IsCheckedChanged += (_, _) => Safe(() =>
        {
            if (_loading || (ShowRegionFrames.IsChecked == true) == Settings.ShowRegionFrames)
                return;
            Host.SetRegionFramesEnabled(ShowRegionFrames.IsChecked == true); // applies and saves
        });
    }

    private void LoadAll()
    {
        LoadProfiles(force: true);
        _loading = true;
        try
        {
            TargetLang.Text = Settings.TargetLang;
            WebEnabled.IsChecked = Settings.WebEnabled;
            Port.Text = Settings.WebPort.ToString(CultureInfo.InvariantCulture);
            KeepAwake.IsChecked = Settings.KeepAwake;
            ShowRegionFrames.IsChecked = Settings.ShowRegionFrames;
            foreach (var action in HotkeyOrder)
                _hotkeyRows[action].Box.Text = Settings.Hotkeys.GetValueOrDefault(action) ?? "";
            LoadTranslation();
            LoadTv();
        }
        finally
        {
            _loading = false;
        }
        LoadWebState();
        LoadTvState();
        RefreshStatus();
    }

    // ---- profiles -----------------------------------------------------------------------------------------------

    /// <summary>Syncs the list and, when the active profile changed (or <paramref name="force"/>), the editor.</summary>
    private void LoadProfiles(bool force)
    {
        var names = Settings.Profiles.Select(p => p.Name).ToArray();
        var active = Settings.GetActive();
        _loading = true;
        try
        {
            if (force || !names.SequenceEqual(_shownProfiles))
            {
                ProfileList.ItemsSource = names;
                _shownProfiles = names;
            }
            ProfileList.SelectedItem = active.Name;
            bool profileChanged = force || _shownActive != active.Name;
            if (profileChanged)
            {
                _shownActive = active.Name;
                ProfileName.Text = active.Name;
                LoadEditor(active);
            }
            ProfileDelete.IsEnabled = names.Length > 1;
            // Also refreshes the boxes state and placement: the box editor may have changed them.
            LoadZoneBlock(active, profileChanged);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Call with <see cref="_loading"/> set: fills the editor without writing back.</summary>
    private void LoadEditor(GameProfile p)
    {
        int lang = SourceLanguages.All.ToList().FindIndex(l => l.Code == p.SourceLang);
        SourceLang.SelectedIndex = Math.Max(lang, 0);

        if (!string.IsNullOrWhiteSpace(p.FontFamily) && !_fonts.Contains(p.FontFamily))
        {
            _fonts = [p.FontFamily, .. _fonts];
            FontFamilyBox.ItemsSource = _fonts;
        }
        FontFamilyBox.SelectedItem = p.FontFamily;
        FontSizeBox.Value = (decimal)Math.Clamp(p.FontSize, MinFontSize, MaxFontSize);
        MinDisplayBox.Value = (decimal)Math.Clamp(p.MinDisplaySeconds, 0, GameProfile.MinDisplayMax);
        OpacitySlider.Value = Math.Clamp(p.BackgroundOpacity, 0, 1);
        OpacityText.Text = FormatPercent(OpacitySlider.Value);
        GameTitle.Text = p.GameTitle ?? "";
        LoadLibraryChoices(p);
    }

    // ---- zones --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Call with <see cref="_loading"/> set: fills the zone block from the selected zone (the profile's last one when
    /// the profile changed or the selection is gone). Text boxes being typed in are left alone.
    /// </summary>
    private void LoadZoneBlock(GameProfile p, bool profileChanged)
    {
        var zone = (profileChanged ? null : p.GetZone(_zoneId)) ?? p.GetLastZone();
        // Keep a box the user is typing in only while the same zone of the same profile stays selected.
        bool sameZone = ReferenceEquals(_textsProfile, p) && _textsZoneId == zone.Id;
        _zoneId = zone.Id;

        string[] names = [.. p.Zones.Select(z => z.Name)];
        string key = string.Join("\n", p.Zones.Select(z => z.Id + z.Name));
        if (key != _shownZones)
        {
            _shownZones = key;
            ZonePicker.ItemsSource = names;
        }
        ZonePicker.SelectedIndex = p.Zones.IndexOf(zone);
        DeleteZone.IsEnabled = p.Zones.Count > 1;

        if (!(sameZone && ZoneName.IsFocused))
            ZoneName.Text = zone.Name;
        Placement.SelectedIndex = Math.Max(Array.IndexOf(Placements, zone.Placement), 0);
        Filter.SelectedIndex = Math.Max(Array.IndexOf(Filters, zone.Filter), 0);
        ZoneSendToWeb.IsChecked = zone.SendToWeb;
        ZoneLearn.IsChecked = zone.LearnToLibrary;
        SpeakerLinesOnly.IsChecked = zone.SpeakerLinesOnly;
        ZoneTrigger.IsChecked = zone.TriggerEnabled;
        if (!(sameZone && ZoneTriggerWords.IsFocused))
            ZoneTriggerWords.Text = zone.TriggerWords;
        _textsProfile = p;
        _textsZoneId = zone.Id;
        ZoneTriggerNoBox.IsVisible = zone.TriggerEnabled && zone.TriggerRegion is null;
        ZoneBoxesState.Text = Loc.Format("ZoneBoxesStateFormat",
            Mark(zone.Region is not null), Mark(zone.GetCaptionRegion() is not null), Mark(zone.TriggerRegion is not null));
    }

    private static string Mark(bool set) => set ? "✓" : "✗";

    /// <summary>Refills the zone block after an edit that changes what it shows (name, trigger, list).</summary>
    private void ReloadZoneBlock()
    {
        bool was = _loading;
        _loading = true;
        try
        {
            LoadZoneBlock(Settings.GetActive(), profileChanged: false);
        }
        finally
        {
            _loading = was;
        }
    }

    /// <summary>Edits the selected zone like <see cref="EditActive"/> edits the profile.</summary>
    private void EditZone(Action<OcrZone> edit, Action? afterApply = null) => EditActive(p =>
    {
        if (p.GetZone(_zoneId) is { } zone)
            edit(zone);
    }, afterApply);

    private void PickZone()
    {
        if (_loading || _closed)
            return;
        var p = Settings.GetActive();
        if (ZonePicker.SelectedIndex is not (>= 0 and var i) || i >= p.Zones.Count || p.Zones[i].Id == _zoneId)
            return;
        _zoneId = p.Zones[i].Id;
        ReloadZoneBlock();
    }

    private void AddNewZone() => EditActive(p =>
    {
        var zone = new OcrZone { Name = OcrZone.NextName(p.Zones) };
        p.Zones.Add(zone);
        _zoneId = zone.Id;
    }, afterApply: ReloadZoneBlock);

    private void DeleteSelectedZone() => EditActive(p =>
    {
        if (p.Zones.Count <= 1 || p.GetZone(_zoneId) is not { } zone)
            return;
        int index = p.Zones.IndexOf(zone);
        p.Zones.RemoveAt(index);
        if (p.LastZoneId == zone.Id)
            p.LastZoneId = null;
        _zoneId = p.Zones[Math.Min(index, p.Zones.Count - 1)].Id;
    }, afterApply: ReloadZoneBlock);

    /// <summary>The zone the name and trigger-words boxes were filled for, or null when it is gone or is not the selected one.</summary>
    private OcrZone? TextsZone()
    {
        if (_loading || _closed)
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
        string name = (ZoneName.Text ?? "").Trim();
        if (name.Length == 0)
            name = zone.Name;
        if (ZoneName.Text != name)
            ZoneName.Text = name;
        if (zone.Name != name)
            EditZone(z => z.Name = name, afterApply: ReloadZoneBlock);
    }

    /// <summary>LostFocus/Enter on the trigger words: saved as typed.</summary>
    private void CommitZoneTriggerWords()
    {
        if (TextsZone() is not { } zone)
            return;
        string words = ZoneTriggerWords.Text ?? "";
        if (zone.TriggerWords != words)
            EditZone(z => z.TriggerWords = words);
    }

    /// <summary>
    /// Call with <see cref="_loading"/> set: lists the libraries found (bundled and downloaded) and selects the profile's.
    /// A library the profile names but that is missing stays listed by its folder name, so the choice is not lost.
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
        LibraryGame.ItemsSource = items;
        LibraryGame.SelectedIndex = index;
        UpdateLibraryNote();
    }

    /// <summary>The library dropdown: the first item (ไม่ใช้คลัง) is null. Saved like the game title, via <see cref="EditActive"/>.</summary>
    private void CommitLibraryGame()
    {
        if (_loading || _closed)
            return;
        if (LibraryGame.SelectedIndex is not (>= 0 and var i) || i >= _libraryGames.Length)
            return;
        string? game = _libraryGames[i];
        if (Settings.GetActive().LibraryGame == game)
            return;
        EditActive(p => p.LibraryGame = game, afterApply: UpdateLibraryNote);
    }

    /// <summary>
    /// The note under the library dropdown: why the library cannot apply (not English → Thai). Empty without a library
    /// or when it applies.
    /// </summary>
    private void UpdateLibraryNote()
    {
        if (_closed)
            return;
        string? game = Settings.GetActive().LibraryGame;
        string text = !string.IsNullOrWhiteSpace(game) && !Host.LibraryLanguagesApply ? Loc.Get("LibraryNeedsEnTh") : "";
        if (LibraryNote.Text != text)
            LibraryNote.Text = text;
        LibraryNote.IsVisible = text.Length > 0;
        OpenLibraryFolder.IsEnabled = AppHost.IsLibraryName(game);
    }

    /// <summary>Opens the game's folder of downloaded libraries in Explorer, creating it first.</summary>
    private void OpenLibraryFolderInExplorer()
    {
        string? game = Settings.GetActive().LibraryGame;
        if (!AppHost.IsLibraryName(game))
            return;
        string dir = Host.LibraryStore.UserDir(game!.Trim());
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true })?.Dispose();
    }

    /// <summary>Edits the active profile, applies at once and saves shortly after. Ignored while loading.</summary>
    private void EditActive(Action<GameProfile> edit, Action? afterApply = null) => Safe(() =>
    {
        if (_loading || _closed)
            return;
        edit(Settings.GetActive());
        Host.ApplySettings();
        ScheduleSave();
        afterApply?.Invoke();
    });

    /// <summary>LostFocus/Enter on the game title: blank clears it. Goes through <see cref="EditActive"/>.</summary>
    private void CommitGameTitle()
    {
        if (_loading || _closed)
            return;
        string title = (GameTitle.Text ?? "").Trim();
        string? value = title.Length == 0 ? null : title;
        if (GameTitle.Text != title)
            GameTitle.Text = title;
        if (Settings.GetActive().GameTitle == value)
            return;
        EditActive(p => p.GameTitle = value);
    }

    private void AddProfile()
    {
        string name = (ProfileName.Text ?? "").Trim();
        if (!ValidateProfileName(name, except: null))
            return;
        Settings.Profiles.Add(GameProfile.Create(name));
        Settings.ActiveProfile = name;
        CommitProfiles();
    }

    private void RenameProfile()
    {
        string name = (ProfileName.Text ?? "").Trim();
        var active = Settings.GetActive();
        if (name == active.Name)
        {
            ProfileError.IsVisible = false;
            return;
        }
        if (!ValidateProfileName(name, except: active))
            return;
        active.Name = name;
        Settings.ActiveProfile = name; // the list's selection is always the active profile
        CommitProfiles();
    }

    private void DeleteProfile()
    {
        if (Settings.Profiles.Count <= 1)
            return;
        Settings.Profiles.Remove(Settings.GetActive());
        Settings.ActiveProfile = Settings.Profiles[0].Name;
        CommitProfiles();
    }

    private bool ValidateProfileName(string name, GameProfile? except)
    {
        string? error = null;
        if (name.Length == 0)
            error = Loc.Get("ProfileNameEmpty");
        else if (Settings.Profiles.Any(p => p != except && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            error = Loc.Get("ProfileNameTaken");
        ProfileError.Text = error;
        ProfileError.IsVisible = error is not null;
        return error is null;
    }

    private void CommitProfiles()
    {
        ProfileError.IsVisible = false;
        Host.ApplySettings();
        Host.SaveSettings();
        LoadProfiles(force: true);
    }

    // ---- OCR languages ------------------------------------------------------------------------------------------

    private async Task RefreshOcrRowsAsync()
    {
        try
        {
            _shownInstalling = string.Join(",",
                SourceLanguages.All.Where(Host.IsInstallingOcrLanguage).Select(l => l.Code));
            foreach (var row in _ocrRows)
            {
                if (Host.IsInstallingOcrLanguage(row.Language))
                {
                    row.ShowInstalling();
                    continue;
                }
                bool available = await Host.IsOcrLanguageAvailableAsync(row.Language);
                if (_closed)
                    return;
                if (!Host.IsInstallingOcrLanguage(row.Language))
                    row.ShowAvailable(available);
            }
        }
        catch (Exception)
        {
            // Rows keep their last state.
        }
    }

    private async Task InstallOcrAsync(OcrRow row)
    {
        try
        {
            row.Message.IsVisible = false;
            row.ShowInstalling();
            bool ok = await Host.InstallOcrLanguageAsync(row.Language);
            if (_closed)
                return;
            bool available = await Host.IsOcrLanguageAvailableAsync(row.Language);
            if (_closed)
                return;
            row.ShowAvailable(available);
            row.Message.Text = Loc.Get(!ok ? "OcrInstallFailed" : available ? "OcrInstallDone" : "OcrInstallNeedsRestart");
            row.Message.IsVisible = true;
            _ = RefreshStatusOcrAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Settings: OCR install row failed", ex); // the row stays as it was
        }
    }

    private void CommitTargetLanguage()
    {
        if (_loading)
            return;
        string code = (TargetLang.Text ?? "").Trim();
        bool valid = LanguageCode.IsMatch(code);
        TargetLangError.IsVisible = !valid;
        if (!valid || code == Settings.TargetLang)
            return;
        Settings.TargetLang = code;
        Host.ApplySettings();
        ScheduleSave();
        UpdateLibraryNote();
    }

    // ---- translation (AI) ---------------------------------------------------------------------------------------

    /// <summary>Call with <see cref="_loading"/> set: fills the translation tab without writing back.</summary>
    private void LoadTranslation()
    {
        var t = Settings.Translation;
        ModeGoogle.IsChecked = t.Mode != TranslationMode.Ai;
        ModeAi.IsChecked = t.Mode == TranslationMode.Ai;
        ShowTranslationMode();
        AiProviderBox.SelectedIndex = Math.Max(Array.IndexOf(AiProviders, t.Provider), 0);
        AiContext.Value = Math.Clamp(t.ContextLines, 0, 10);
        AiQueue.Value = Math.Clamp(t.MaxWaitingLines, AiRefiner.MinMaxWaiting, AiRefiner.MaxMaxWaiting);
        AiParallel.Value = Math.Clamp(t.ParallelRequests, AiRefiner.MinMaxParallel, AiRefiner.MaxMaxParallel);
        LoadProviderFields();
    }

    /// <summary>
    /// Call with <see cref="_loading"/> set: shows the selected provider's model, URL and key state. The key box is
    /// always emptied; a stored key only shows as the <see cref="StoredKeyMask"/> placeholder.
    /// </summary>
    private void LoadProviderFields()
    {
        var provider = Settings.Translation.Provider;
        var ps = ProviderSettings(provider);
        bool ollama = provider == AiProvider.Ollama;

        AiKeyLabelText.IsVisible = !ollama;
        AiKey.IsVisible = !ollama;
        AiKey.Text = "";
        AiKey.PlaceholderText = ps.ApiKeyProtected is not null ? StoredKeyMask : null;
        AiModel.Text = ps.Model;
        AiUrl.Text = ps.BaseUrl;
        AiListModels.IsVisible = ollama;
        AiListModels.IsEnabled = true;
        AiModelList.ItemsSource = null;
        AiModelList.IsVisible = false;
        AiListError.IsVisible = false;
        AiTest.IsEnabled = true;
        AiTestResult.IsVisible = false;
        _aiTestSeq++; // a test or list still running belongs to the previous provider: drop its result
        _aiListSeq++;
        if (provider == AiProvider.Custom && string.IsNullOrWhiteSpace(ps.BaseUrl))
            AiAdvancedExpander.IsExpanded = true; // the URL is required here and lives under "ขั้นสูง"
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
        if (_loading || _closed || button.IsChecked != true || Settings.Translation.Mode == mode)
            return;
        Settings.Translation.Mode = mode;
        ShowTranslationMode();
        Host.ApplyTranslationSettings(); // rebuilds the translator handle: new generation, status cleared
    }

    private bool GoogleMode => Settings.Translation.Mode != TranslationMode.Ai;

    /// <summary>Shows the AI settings only in AI mode, and the Google privacy note only in Google mode.</summary>
    private void ShowTranslationMode()
    {
        AiSettingsPanel.IsVisible = !GoogleMode;
        GooglePrivacyText.IsVisible = GoogleMode;
        TranslationStatusLabel.Text = Loc.Get(GoogleMode ? "TranslationStatusLabel" : "AiStatusLabel");
    }

    private void SelectAiProvider()
    {
        if (_loading || _closed)
            return;
        if (AiProviderBox.SelectedIndex is not (>= 0 and var i) || i >= AiProviders.Length)
            return;
        var provider = AiProviders[i];
        if (provider == Settings.Translation.Provider)
            return;
        Settings.Translation.Provider = provider;
        _loading = true;
        try
        {
            LoadProviderFields();
        }
        finally
        {
            _loading = false;
        }
        Host.ApplyTranslationSettings();
    }

    /// <summary>
    /// LostFocus/Enter on the key, model or URL box (and window close): writes what changed for the selected provider.
    /// The key is trimmed, protected and the box emptied; a key that is blank after trimming saves nothing (R4).
    /// </summary>
    private void CommitAiFields()
    {
        if (_loading || _closed)
            return;
        var provider = Settings.Translation.Provider;
        var ps = ProviderSettings(provider);
        bool changed = false;

        if (AiKey.IsVisible && TypedKey() is { } key)
        {
            ps.ApiKeyProtected = Host.Secrets.Protect(key);
            changed = true;
        }
        if (!string.IsNullOrEmpty(AiKey.Text))
            AiKey.Text = "";
        AiKey.PlaceholderText = ps.ApiKeyProtected is not null ? StoredKeyMask : null;

        string model = (AiModel.Text ?? "").Trim();
        if (AiModel.Text != model)
            AiModel.Text = model;
        if (model != ps.Model)
        {
            ps.Model = model;
            changed = true;
        }

        string url = EffectiveUrl(provider);
        if (AiUrl.Text != url)
            AiUrl.Text = url;
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
        if (_loading || _closed || AiContext.Value is not { } v)
            return;
        int lines = (int)Math.Clamp(Math.Round(v), 0, 10);
        if (lines == Settings.Translation.ContextLines)
            return;
        Settings.Translation.ContextLines = lines;
        Host.ApplyTranslationSettings();
    }

    private void CommitAiQueue()
    {
        if (_loading || _closed || AiQueue.Value is not { } v)
            return;
        int lines = (int)Math.Clamp(Math.Round(v), AiRefiner.MinMaxWaiting, AiRefiner.MaxMaxWaiting);
        if (lines == Settings.Translation.MaxWaitingLines)
            return;
        Settings.Translation.MaxWaitingLines = lines;
        Host.ApplyTranslationSettings();
    }

    private void CommitAiParallel()
    {
        if (_loading || _closed || AiParallel.Value is not { } v)
            return;
        int requests = (int)Math.Clamp(Math.Round(v), AiRefiner.MinMaxParallel, AiRefiner.MaxMaxParallel);
        if (requests == Settings.Translation.ParallelRequests)
            return;
        Settings.Translation.ParallelRequests = requests;
        Host.ApplyTranslationSettings();
    }

    /// <summary>The trimmed key typed in the box, or null when it is blank (never "").</summary>
    private string? TypedKey() => (AiKey.Text ?? "").Trim() is { Length: > 0 } key ? key : null;

    /// <summary>The URL box, trimmed; blank means the preset URL (Custom has none).</summary>
    private string EffectiveUrl(AiProvider provider)
    {
        string url = (AiUrl.Text ?? "").Trim();
        return url.Length == 0 ? AiProviderPresets.Get(provider).BaseUrl : url;
    }

    private void UpdateHttpWarning()
    {
        if (_closed)
            return;
        bool keySet = (AiKey.IsVisible && TypedKey() is not null)
            || ProviderSettings(Settings.Translation.Provider).ApiKeyProtected is not null;
        bool warn = keySet && IsCleartextToRemoteHost(AiUrl.Text);
        AiHttpWarningText.IsVisible = warn;
        if (warn)
            AiAdvancedExpander.IsExpanded = true;
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

    /// <summary>The translator status (Google or AI), in the translation tab and as the translation line of the status list.</summary>
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
        AiStatusText.Text = text;
        AiStatusText.Classes.Set("ok", !down);
        AiStatusText.Classes.Set("error", down);
        StatusTranslator.Text = Loc.Get(GoogleMode ? "TranslationStatusLabel" : "AiStatusLabel") + ": " + text;
    }

    /// <summary>A known translator copy key (AI, or Google's <c>GoogleDown</c>), or <c>AiDown</c>: never shows an unexpected string.</summary>
    private static string AiCopyKey(string? key) => key is not null && AiCopyKeys.Contains(key) ? key : AiRefiner.AiDownKey;

    /// <summary>Sends the fixed test sentence with the fields as typed (unsaved key included) and saves nothing.</summary>
    private async Task TestAiAsync()
    {
        try
        {
            if (_closed)
                return;
            int seq = ++_aiTestSeq;
            var draft = BuildTestDraft();
            string? typedKey = AiKey.IsVisible ? TypedKey() : null;
            string target = LanguageNames.ForTarget(
                string.IsNullOrWhiteSpace(Settings.TargetLang) ? "th" : Settings.TargetLang.Trim());
            var source = Host.AiSource;
            var ct = _closeCts.Token;

            AiTest.IsEnabled = false;
            ShowTestResult(Loc.Get("AiTesting"), ok: null);
            var outcome = await Task.Run(() => RunAiTestAsync(source, draft, typedKey, target, ct), CancellationToken.None);
            if (_closed || seq != _aiTestSeq)
                return;
            AiTest.IsEnabled = true;
            if (outcome.ErrorKey is { } key)
                ShowTestResult(Loc.Get(AiCopyKey(key)) + (outcome.Detail is { } d ? "\n" + d : ""), ok: false);
            else
                ShowTestResult(Loc.Format("AiTestOk", outcome.Seconds.ToString("0.0", CultureInfo.InvariantCulture))
                    + " — " + outcome.Text, ok: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings: AI test display failed: {ex.GetType().Name}");
            if (!_closed)
                Safe(() =>
                {
                    AiTest.IsEnabled = true;
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
                    Model = (AiModel.Text ?? "").Trim(),
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
        AiTestResult.Text = text;
        AiTestResult.Classes.Set("ok", ok == true);
        AiTestResult.Classes.Set("error", ok == false);
        AiTestResult.IsVisible = true;
    }

    /// <summary>Ollama: fills the model dropdown from <c>/api/tags</c> at the URL as typed.</summary>
    private async Task ListOllamaModelsAsync()
    {
        try
        {
            if (_closed)
                return;
            int seq = ++_aiListSeq;
            AiListError.IsVisible = false;
            if (!Uri.TryCreate(EffectiveUrl(AiProvider.Ollama), UriKind.Absolute, out var baseUrl)
                || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
            {
                ShowListError(AiRefiner.AiDownKey);
                return;
            }
            var http = Host.AiHttp;
            var ct = _closeCts.Token;
            AiListModels.IsEnabled = false;
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
            if (_closed || seq != _aiListSeq)
                return;
            AiListModels.IsEnabled = true;
            if (names is null || names.Count == 0)
            {
                ShowListError(names is null ? AiRefiner.AiDownKey : "AiNoModel");
                return;
            }
            string current = (AiModel.Text ?? "").Trim();
            _loading = true;
            try
            {
                AiModelList.ItemsSource = names.ToList();
                AiModelList.SelectedItem = names.Contains(current) ? current : null;
            }
            finally
            {
                _loading = false;
            }
            AiModelList.IsVisible = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings: Ollama model list display failed: {ex.GetType().Name}");
            if (!_closed)
                Safe(() => AiListModels.IsEnabled = true);
        }
    }

    private void ShowListError(string key)
    {
        AiListError.Text = Loc.Get(key);
        AiListError.IsVisible = true;
    }

    private void PickOllamaModel()
    {
        if (_loading || _closed || AiModelList.SelectedItem is not string name)
            return;
        AiModel.Text = name;
        CommitAiFields();
    }

    private sealed record AiTestOutcome(string? ErrorKey, double Seconds, string? Text, string? Detail = null);

    // ---- web ----------------------------------------------------------------------------------------------------

    private void LoadWebState()
    {
        _loading = true;
        try
        {
            WebEnabled.IsChecked = Settings.WebEnabled;
        }
        finally
        {
            _loading = false;
        }
        bool running = Host.IsWebRunning;
        WebState.Text = Loc.Get(running ? "WebRunning" : "WebStopped");
        WebState.Classes.Set("ok", running);
        WebClientsText.Text = Loc.Format("WebClients", Host.WebClients);
    }

    private async Task SetWebEnabledAsync()
    {
        try
        {
            if (_loading || _closed)
                return;
            await Host.SetWebEnabledAsync(WebEnabled.IsChecked == true);
            Host.SaveSettings();
            if (!_closed)
                LoadWebState();
        }
        catch (Exception)
        {
            // AppHost reports start failures with a notification.
        }
    }

    private async Task ApplyPortAsync()
    {
        try
        {
            string text = (Port.Text ?? "").Trim();
            bool valid = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
                && port is >= MinPort and <= MaxPort;
            PortError.IsVisible = !valid;
            if (!valid || _webBusy)
                return;
            Port.Text = port.ToString(CultureInfo.InvariantCulture);

            _webBusy = true;
            PortApply.IsEnabled = false;
            try
            {
                bool changed = port != Settings.WebPort;
                Settings.WebPort = port;
                // Restarts the server on the new port; on the same port it retries a server that failed to start.
                await Host.SetWebEnabledAsync(Settings.WebEnabled);
                Host.SaveSettings();
                if (_closed)
                    return;
                LoadWebState();
                if (changed)
                {
                    _ = RefreshLanAsync();
                    _ = CheckFirewallAsync();
                }
            }
            finally
            {
                _webBusy = false;
                if (!_closed)
                    PortApply.IsEnabled = true;
            }
        }
        catch (Exception)
        {
            // AppHost reports start failures with a notification.
        }
    }

    private async Task RefreshLanAsync()
    {
        try
        {
            int seq = ++_lanSeq;
            int port = Settings.WebPort;
            var candidates = await Task.Run(() =>
            {
                try
                {
                    return LanAddresses.GetCandidates();
                }
                catch (Exception)
                {
                    return (IReadOnlyList<IPAddress>)[];
                }
            });
            if (_closed || seq != _lanSeq)
                return;

            var urls = candidates.Select(ip => $"http://{ip}:{port}/").ToList();
            UrlList.Children.Clear();
            foreach (var url in urls)
                UrlList.Children.Add(new SelectableTextBlock { Text = url, FontFamily = new FontFamily("Consolas, Segoe UI"), FontSize = 15 });
            NoLan.IsVisible = urls.Count == 0;
            ShowQr(urls.Count > 0 ? urls[0] : null);
        }
        catch (Exception)
        {
            // The list stays as it was.
        }
    }

    private void ShowQr(string? url)
    {
        QrImage.Source = null;
        _qr?.Dispose();
        _qr = null;
        QrFrame.IsVisible = false;
        if (url is null)
            return;

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var code = new PngByteQRCode(data);
        byte[] png = code.GetGraphic(8);
        using var stream = new MemoryStream(png);
        _qr = new Bitmap(stream);
        QrImage.Source = _qr;
        QrFrame.IsVisible = true;
    }

    /// <summary><see cref="Core.Platform.ILanAccessSetup.IsPortOpen"/> blocks for up to 10 s: thread pool only.</summary>
    private async Task CheckFirewallAsync()
    {
        try
        {
            var lan = Host.LanAccess;
            if (lan is null)
            {
                // This OS has no per-port firewall rule to set up: no firewall row.
                FirewallRow.IsVisible = false;
                NetworkHintText.IsVisible = false; // it talks about the Windows network profile and firewall
                FirewallCancelHint.IsVisible = false;
                return;
            }
            int seq = ++_firewallSeq;
            int port = Settings.WebPort;
            FirewallState.Text = Loc.Get("FirewallChecking");
            FirewallState.Classes.Set("ok", false);
            FirewallState.IsVisible = true;
            FirewallButton.IsVisible = false;

            bool open = await Task.Run(() => lan.IsPortOpen(port));
            if (_closed || seq != _firewallSeq)
                return;
            FirewallState.Text = open ? Loc.Get("FirewallOpen") : "";
            FirewallState.Classes.Set("ok", open);
            FirewallState.IsVisible = open;
            FirewallButton.IsVisible = !open;
        }
        catch (Exception)
        {
            // Leave the row as it is.
        }
    }

    private async Task OpenFirewallAsync()
    {
        try
        {
            FirewallError.IsVisible = false;
            FirewallButton.IsEnabled = false;
            bool ok;
            try
            {
                ok = Host.LanAccess is { } lan && await lan.OpenPortAsync(Settings.WebPort);
            }
            catch (Exception ex)
            {
                Log.Warn("Settings: opening the firewall failed", ex);
                ok = false;
            }
            if (_closed)
                return;
            FirewallButton.IsEnabled = true;
            FirewallError.IsVisible = !ok;
            await CheckFirewallAsync();
        }
        catch (Exception)
        {
            // Leave the row as it is.
        }
    }

    // ---- general ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Validates every hotkey box; saves the valid, unique ones and shows an inline error on the others (which keep
    /// their saved value). AppHost re-registers when the set changes.
    /// </summary>
    private void CommitHotkeys()
    {
        if (_closed)
            return;
        var typed = HotkeyOrder.ToDictionary(a => a, a => TryNormalize(_hotkeyRows[a].Box.Text));
        var effective = HotkeyOrder.ToDictionary(a => a,
            a => typed[a] ?? TryNormalize(Settings.Hotkeys.GetValueOrDefault(a)) ?? Settings.Hotkeys.GetValueOrDefault(a));

        bool changed = false;
        foreach (var action in HotkeyOrder)
        {
            var (box, error) = _hotkeyRows[action];
            string? key = null;
            if (typed[action] is not { } spec)
                key = "HotkeyInvalid";
            else if (HotkeyOrder.Any(other => other != action && effective[other] == spec))
                key = "HotkeyDuplicate";

            error.Text = key is null ? "" : Loc.Get(key);
            error.IsVisible = key is not null;
            if (key is not null)
                continue;

            if (box.Text != typed[action])
                box.Text = typed[action];
            if (Settings.Hotkeys.GetValueOrDefault(action) != typed[action])
            {
                Settings.Hotkeys[action] = typed[action]!;
                changed = true;
            }
        }
        if (!changed)
            return;
        Host.ApplySettings();
        Host.SaveSettings();
    }

    private static string? TryNormalize(string? text)
    {
        try
        {
            return HotkeySpec.Parse(text ?? "").ToString();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private void RefreshStatus()
    {
        if (_closed)
            return;
        var status = Host.Status;
        var now = DateTimeOffset.UtcNow;
        StatusOverall.Text = Host.StatusText;
        bool card = Host.PictureSource == PictureSource.CaptureCard;
        // With the capture card, the overall line already says what the card is doing.
        StatusRemotePlay.IsVisible = !card;
        StatusRemotePlay.Text = Loc.Get(Host.TargetWindow is not null ? "StatusRemotePlayFound" : "NoRemotePlay");

        if (status.Minimized)
            StatusFrames.Text = Loc.Get(card ? "CaptureMinimized" : "Minimized");
        else if (status.FramesStalled)
            StatusFrames.Text = Loc.Get(card ? "CaptureFramesStalled" : "FramesStalled");
        else if (IncomingFrameSize(card) is (int width, int height))
        {
            long age = Host.LastFrameAt is { } at ? Math.Max(0, (long)(now - at).TotalSeconds) : 0;
            StatusFrames.Text = Loc.Format("StatusFrames", width, height, age);
        }
        else
            StatusFrames.Text = Loc.Get("NoFrameYet");

        RefreshAiStatus(status, now); // also fills StatusTranslator
        UpdateLibraryNote();
    }

    /// <summary>
    /// The size of the frames coming in, or null before the first. With the capture card it is the mode being read, so
    /// this once-a-second line never reads a frame back from the GPU on the UI thread.
    /// </summary>
    private (int Width, int Height)? IncomingFrameSize(bool card)
    {
        if (!card)
            return Host.LatestFrame is { } frame ? (frame.Width, frame.Height) : null;
        return Host.LastFrameAt is not null && Host.CaptureCard?.CurrentMode is { } mode ? (mode.Width, mode.Height) : null;
    }

    private async Task RefreshStatusOcrAsync()
    {
        try
        {
            int seq = ++_statusOcrSeq;
            var profile = Settings.GetActive();
            var language = SourceLanguages.All.FirstOrDefault(l => l.Code == profile.SourceLang) ?? SourceLanguages.All[0];
            bool available = await Host.IsOcrLanguageAvailableAsync(language);
            if (_closed || seq != _statusOcrSeq)
                return;
            StatusOcr.Text = Loc.Format("StatusOcr", language.ThaiName, Loc.Get(available ? "OcrReady" : "OcrMissing"));
        }
        catch (Exception)
        {
            // Keep the last line.
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushSave()
    {
        if (!_saveTimer.IsEnabled)
            return;
        _saveTimer.Stop();
        Host.SaveSettings();
    }

    private static string FormatPercent(double value) =>
        (Math.Clamp(value, 0, 1) * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // A settings control must not take the app down; the next refresh redraws from current state.
            // Type only: a message could echo a typed key, URL or test text.
            Log.Warn($"Settings: control handler failed: {ex.GetType().Name}");
        }
    }

    /// <summary>One row of the OCR language tab: name, then "พร้อมใช้" or an install button, then a result message.</summary>
    private sealed class OcrRow
    {
        public OcrRow(SourceLanguage language)
        {
            Language = language;
            var name = new TextBlock
            {
                Text = $"{language.ThaiName} ({language.OcrTag})",
                VerticalAlignment = VerticalAlignment.Center,
                Width = 200,
            };
            Ready = new TextBlock { Text = Loc.Get("OcrChecking"), VerticalAlignment = VerticalAlignment.Center };
            Install = new Button { Content = Loc.Get("OcrInstall"), IsVisible = false };
            Message = new TextBlock
            {
                IsVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 0, 0, 0),
            };
            Message.Classes.Add("hint");

            var status = new Panel { MinWidth = 120 };
            status.Children.Add(Ready);
            status.Children.Add(Install);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), MinHeight = 36 };
            Grid.SetColumn(status, 1);
            Grid.SetColumn(Message, 2);
            grid.Children.Add(name);
            grid.Children.Add(status);
            grid.Children.Add(Message);
            Root = grid;
        }

        public SourceLanguage Language { get; }

        public Control Root { get; }

        public TextBlock Ready { get; }

        public Button Install { get; }

        public TextBlock Message { get; }

        public void ShowInstalling()
        {
            Ready.IsVisible = false;
            Install.IsVisible = true;
            Install.IsEnabled = false;
            Install.Content = Loc.Get("OcrInstalling");
        }

        public void ShowAvailable(bool available)
        {
            Ready.Text = Loc.Get("OcrReady");
            Ready.Classes.Set("ok", available);
            Ready.IsVisible = available;
            Install.IsVisible = !available;
            Install.IsEnabled = true;
            Install.Content = Loc.Get("OcrInstall");
        }
    }
}
