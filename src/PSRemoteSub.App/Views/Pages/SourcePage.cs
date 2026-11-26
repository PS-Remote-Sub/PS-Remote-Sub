using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using PSRemoteSub.App.Views.Controls;
using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App.Views.Pages;

/// <summary>
/// "ภาพจากเกม" (mockup New-Settings-Source): Remote Play or the capture card as two option cards with status pills,
/// the card's device and mode with "เปิดหน้าต่างดูภาพ", and the HDCP / HDR help. Logic ported from
/// <c>PictureSourcePanel</c>: the source goes through <see cref="AppHost.SetPictureSource"/>; a device or mode change is
/// written to <see cref="AppSettings.Capture"/> and applied with <see cref="AppHost.ApplyCaptureSettings"/>.
/// </summary>
/// <remarks>
/// UI thread only. The device and mode lists load each time the page is shown. Device listing opens no device; a device
/// is opened to list its modes only when the source is the capture card and the capture window is closed (with the
/// window open the modes come from the card, with Remote Play no device is opened). A newer request, or leaving the
/// page, drops an older one's result. When the card is unavailable on this PC the card choice and lists are disabled
/// with the existing note. Status pills show only what the app knows.
/// </remarks>
public sealed class SourcePage : SettingsPage
{
    private const string MonitorGlyph = "M3 5h18v12H3zM8 21h8M12 17v4";
    private const string ChipGlyph = "M7 7h10v10H7zM10 3v4M14 3v4M10 17v4M14 17v4M3 10h4M3 14h4M17 10h4M17 14h4";
    private const string EyeGlyph = "M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12zM12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z";
    private const string CheckGlyph = "M5 12.5l4.5 4.5L19 7.5";
    private const string WarnGlyph = "M12 3 2 20h20zM12 10v4.5M12 17.5h.01";
    private const string ArrowGlyph = "M9 6l6 6-6 6";
    private const string RefreshGlyph = "M20 11.5a8 8 0 1 1-2.4-5.7M20 4v5h-5";
    private const string OpenGlyph = "M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5";

    private readonly RadioButton _remoteChoice = new() { Classes = { "opt" }, GroupName = "PictureSource" };
    private readonly RadioButton _cardChoice = new() { Classes = { "opt" }, GroupName = "PictureSource" };
    private readonly CardParts _remoteParts;
    private readonly CardParts _cardParts;
    private readonly Border _remotePill = NewPill();
    private readonly TextBlock _remotePillText = new();
    private readonly Border _cardPill = NewPill();
    private readonly TextBlock _cardPillText = new();
    private readonly Border _unavailableNote;
    private readonly ComboBox _deviceBox = new() { Classes = { "sel" }, PlaceholderText = "—", IsEnabled = false };
    private readonly Button _refresh;
    private readonly ComboBox _modeBox = new() { Classes = { "sel" } };
    private readonly Button _openWindow;

    private readonly List<CaptureModeSetting?> _modes = [null];
    private IReadOnlyList<CaptureDevice> _devices = [];
    private IReadOnlyList<CaptureMode>? _shownOffered;
    private int _deviceSeq;
    private int _modeSeq;
    private bool _filling;  // true while the page fills its controls: their change events are not the user's

    public SourcePage(AppHost host) : base(host)
    {
        _modeBox.ItemsSource = new[] { Loc.Get("CaptureModeAuto") };
        _modeBox.SelectedIndex = 0;

        _remoteParts = SourceCard(_remoteChoice, MonitorGlyph, "PS Remote Play", Loc.Get("SourceRemotePlaySub"), _remotePill, _remotePillText);
        _cardParts = SourceCard(_cardChoice, ChipGlyph, "Capture card", Loc.Get("SourceCaptureCardSub"), _cardPill, _cardPillText);
        _remoteChoice.IsCheckedChanged += (_, _) => Safe(() => OnSourceChosen(_remoteChoice, PictureSource.RemotePlay));
        _cardChoice.IsCheckedChanged += (_, _) => Safe(() => OnSourceChosen(_cardChoice, PictureSource.CaptureCard));

        _unavailableNote = new Border
        {
            Classes = { "note", "bad" },
            IsVisible = false,
            Child = new TextBlock { Text = Loc.Get("CaptureUnavailable") },
        };

        _deviceBox.SelectionChanged += (_, _) => Safe(OnDeviceChosen);
        _modeBox.SelectionChanged += (_, _) => Safe(OnModeChosen);
        _refresh = IconButton(Loc.Get("CaptureRefresh"), RefreshGlyph, "btn", () => _ = RefreshAsync());
        _openWindow = IconButton(Loc.Get("SourceOpenWindow"), OpenGlyph, "btn", () => Host.RequestCaptureWindow());

        var deviceRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        deviceRow.Children.Add(_deviceBox);
        Grid.SetColumn(_refresh, 1);
        deviceRow.Children.Add(_refresh);

        var cards = new StackPanel { Spacing = 12, Children = { _remoteChoice, _cardChoice, _unavailableNote } };
        var cardSection = new Border
        {
            Classes = { "section" },
            Child = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new TextBlock { Classes = { "h2" }, Text = "Capture card" },
                    LabeledColumn(Loc.Get("CaptureDevice"), deviceRow),
                    LabeledColumn(Loc.Get("CaptureModeLabel"), _modeBox),
                    _openWindow,
                },
            },
        };

        var help = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 14 };
        var hdcp = HelpTile(EyeGlyph, Loc.Get("SourceHdcpTitle"), Loc.Get("SourceHdcpText"),
            Loc.Get("SourceMenuSettings"), Loc.Get("SourceMenuSystem"), "HDMI", Loc.Get("SourceMenuHdcp"));
        var hdr = HelpTile(EyeGlyph, Loc.Get("SourceHdrTitle"), Loc.Get("SourceHdrText"));
        Grid.SetColumn(hdr, 1);
        help.Children.Add(hdcp);
        help.Children.Add(hdr);
        var fix = new Border
        {
            Classes = { "section" },
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    SectionHeader(Loc.Get("SourceFixTitle"), Loc.Get("SourceFixSub")),
                    help,
                },
            },
        };

        var body = PageBody(PageHeader(Loc.Get("NavSource"), Loc.Get("SourceSub")), cards, cardSection, fix);
        body.MaxWidth = 860;
        body.HorizontalAlignment = HorizontalAlignment.Left;
        SetBody(body);

        DetachedFromVisualTree += (_, _) =>
        {
            _deviceSeq++; // a list still loading is dropped
            _modeSeq++;
        };
    }

    // ---- page hooks -----------------------------------------------------------------------------------------------

    public override void OnShown() => Safe(() =>
    {
        ShowSource();
        bool available = Host.IsCaptureAvailable;
        _cardChoice.IsEnabled = available;
        _unavailableNote.IsVisible = !available;
        _refresh.IsEnabled = available;
        _modeBox.IsEnabled = available;
        _openWindow.IsEnabled = available;
        _deviceBox.IsEnabled = false;
        if (available)
            _ = LoadDevicesAsync();
    });

    protected override void OnHostStateChangedCore()
    {
        ShowSource();
        if (Host.CaptureWindowOpen && Host.CaptureCard is { } card)
        {
            // The card's list changes when it (re)opens a device; the window's toolbar may have picked another mode.
            var offered = card.OfferedModes;
            if (!ReferenceEquals(offered, _shownOffered) || !Equals(SelectedMode(), Settings.Capture.Mode))
            {
                _modeSeq++; // a listing still running is older than this
                ShowModes(offered);
            }
        }
    }

    // ---- source ---------------------------------------------------------------------------------------------------

    private void ShowSource()
    {
        var source = Host.PictureSource;
        _filling = true;
        try
        {
            _remoteChoice.IsChecked = source == PictureSource.RemotePlay;
            _cardChoice.IsChecked = source == PictureSource.CaptureCard;
        }
        finally
        {
            _filling = false;
        }
        RefreshCardVisuals();
        ShowPills(source);
    }

    /// <summary>
    /// Both cards follow their own IsChecked on every change: on Avalonia 12.1.6 the newly checked button raises while
    /// the old one is still checked, so a single handler cannot rely on the other's state.
    /// </summary>
    private void RefreshCardVisuals()
    {
        _remoteParts.Show(_remoteChoice.IsChecked == true);
        _cardParts.Show(_cardChoice.IsChecked == true);
    }

    private void OnSourceChosen(RadioButton button, PictureSource source)
    {
        RefreshCardVisuals();
        if (_filling || button.IsChecked != true)
            return;
        Host.SetPictureSource(source);
    }

    /// <summary>Pills show only what the app knows: nothing for the source that is not in use.</summary>
    private void ShowPills(PictureSource source)
    {
        if (source == PictureSource.RemotePlay)
        {
            if (Host.Status.RemotePlayFound)
                SetPill(_remotePill, _remotePillText, "ok", Loc.Get("SourcePillFound"));
            else
                SetPill(_remotePill, _remotePillText, "warn", Loc.Get("SourcePillNotFound"));
        }
        else
            SetPill(_remotePill, _remotePillText, null, null);

        if (source == PictureSource.CaptureCard)
        {
            var state = Host.CaptureWindowOpen ? Host.CaptureCard?.State : null;
            switch (state)
            {
                case CaptureState.Playing:
                    SetPill(_cardPill, _cardPillText, "ok", Loc.Get("SourcePillSignal"));
                    break;
                case CaptureState.NoSignal:
                    SetPill(_cardPill, _cardPillText, "warn", Loc.Get("SourcePillNoSignal"));
                    break;
                default:
                    SetPill(_cardPill, _cardPillText, "info", Loc.Get("SourcePillInUse"));
                    break;
            }
        }
        else
            SetPill(_cardPill, _cardPillText, null, null);
    }

    private static void SetPill(Border pill, TextBlock text, string? kind, string? label)
    {
        pill.Classes.Remove("ok");
        pill.Classes.Remove("warn");
        pill.Classes.Remove("info");
        pill.IsVisible = kind is not null;
        if (kind is null)
            return;
        pill.Classes.Add(kind);
        text.Text = label;
        if (pill.Child is StackPanel { Children: [StrokeIcon icon, ..] })
            icon.Data = kind == "warn" ? WarnGlyph : CheckGlyph;
    }

    // ---- devices and modes (ported from PictureSourcePanel) -------------------------------------------------------

    /// <summary>"รีเฟรช": lists the devices again and has an open capture window apply them. Never throws.</summary>
    private async Task RefreshAsync()
    {
        try
        {
            await LoadDevicesAsync();
            if (Host is { CaptureWindowOpen: true })
                Host.ApplyCaptureSettings();
        }
        catch (Exception ex)
        {
            Log.Warn("Refreshing capture devices failed", ex);
        }
    }

    /// <summary>
    /// Lists the devices and selects the one the card opens (<see cref="CaptureDeviceRules.ResolveDevice"/>): none when
    /// the saved device is missing, so choosing any device then applies it. Never throws.
    /// </summary>
    private async Task LoadDevicesAsync()
    {
        try
        {
            int seq = ++_deviceSeq;
            if (Host.CaptureCard is not { } card)
                return;
            IReadOnlyList<CaptureDevice> devices;
            try
            {
                devices = await card.ListDevicesAsync(); // enumeration only: opens no device
            }
            catch (Exception ex)
            {
                Log.Warn("Listing capture devices failed", ex);
                devices = [];
            }
            if (seq != _deviceSeq)
                return;

            _devices = devices;
            var selected = CaptureDeviceRules.ResolveDevice(devices, Settings.Capture.DeviceId);
            _filling = true;
            try
            {
                _deviceBox.ItemsSource = devices.Select(d => d.Name).ToList();
                _deviceBox.SelectedIndex = selected is null ? -1 : devices.ToList().IndexOf(selected);
                _deviceBox.IsEnabled = devices.Count > 0;
            }
            finally
            {
                _filling = false;
            }
            await LoadModesAsync(selected);
        }
        catch (Exception ex)
        {
            Log.Warn("Showing capture devices failed", ex);
        }
    }

    /// <summary>
    /// Fills the mode list for <paramref name="device"/>. Opens the device (on the thread pool) only when the source is the
    /// capture card and its window is closed; otherwise the card's known modes are used. Never throws.
    /// </summary>
    private async Task LoadModesAsync(CaptureDevice? device)
    {
        try
        {
            int seq = ++_modeSeq;
            if (Host.CaptureCard is not { } card)
                return;
            IReadOnlyList<CaptureMode> offered;
            if (device is null)
                offered = [];
            else if (Host.CaptureWindowOpen || Host.PictureSource != PictureSource.CaptureCard)
                offered = card.OfferedModes;
            else
            {
                string id = device.Id;
                try
                {
                    offered = await Task.Run(() => card.ListModes(id));
                }
                catch (Exception ex)
                {
                    Log.Warn("Listing capture modes failed", ex);
                    offered = [];
                }
            }
            if (seq != _modeSeq)
                return;
            ShowModes(offered);
        }
        catch (Exception ex)
        {
            Log.Warn("Showing capture modes failed", ex);
        }
    }

    /// <summary>
    /// "อัตโนมัติ" first, then the usable choices; selects the saved mode, or "อัตโนมัติ" when the device does not offer it.
    /// With no modes known, the saved mode is still shown as the choice it is.
    /// </summary>
    private void ShowModes(IReadOnlyList<CaptureMode> offered)
    {
        _shownOffered = offered;
        var wanted = Settings.Capture.Mode;
        var choices = CaptureModeChooser.Choices(offered);
        if (choices.Count == 0 && wanted is not null)
            choices = [wanted];
        int index = wanted is null ? 0 : choices.ToList().IndexOf(wanted) + 1;
        if (_modes.Skip(1).SequenceEqual(choices) && _modeBox.SelectedIndex == index)
            return; // unchanged: leave an open drop-down alone
        _filling = true;
        try
        {
            _modes.Clear();
            _modes.Add(null);
            _modes.AddRange(choices);
            _modeBox.ItemsSource = _modes.Select(CaptureText.ModeLabel).ToList();
            _modeBox.SelectedIndex = index;
        }
        finally
        {
            _filling = false;
        }
    }

    private CaptureModeSetting? SelectedMode() =>
        _modeBox.SelectedIndex is var i && i > 0 && i < _modes.Count ? _modes[i] : null;

    private void OnDeviceChosen()
    {
        if (_filling)
            return;
        int i = _deviceBox.SelectedIndex;
        if (i < 0 || i >= _devices.Count)
            return;
        var device = _devices[i];
        if (device.Id == Settings.Capture.DeviceId)
            return;
        Settings.Capture.DeviceId = device.Id;
        Host.ApplyCaptureSettings(); // saves; an open window reopens on this device
        _ = LoadModesAsync(device);
    }

    private void OnModeChosen()
    {
        if (_filling)
            return;
        int i = _modeBox.SelectedIndex;
        if (i < 0 || i >= _modes.Count)
            return;
        var mode = _modes[i];
        if (Equals(mode, Settings.Capture.Mode))
            return;
        Settings.Capture.Mode = mode;
        Host.ApplyCaptureSettings(); // saves; an open window reopens with this mode
    }

    // ---- building blocks ------------------------------------------------------------------------------------------

    private sealed class CardParts(Ellipse ring, Ellipse dot)
    {
        public void Show(bool on)
        {
            ring.Bind(Shape.StrokeProperty, ring.GetResourceObservable(on ? "AccentBrush" : "Text3Brush"));
            dot.IsVisible = on;
        }
    }

    private static Border NewPill() => new()
    {
        Classes = { "pill" },
        Height = double.NaN,
        MinHeight = 26,
        Padding = new Thickness(10, 4),
        VerticalAlignment = VerticalAlignment.Center,
        IsVisible = false,
    };

    private static CardParts SourceCard(RadioButton button, string glyph, string title, string sub, Border pill, TextBlock pillText)
    {
        var ring = new Ellipse { StrokeThickness = 2 };
        var dot = new Ellipse { Width = 10, Height = 10, IsVisible = false };
        dot.Bind(Shape.FillProperty, dot.GetResourceObservable("AccentBrush"));
        var radio = new Panel { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center, Children = { ring, dot } };

        var iconBox = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(12),
            Child = new StrokeIcon { Data = glyph, Size = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        iconBox.Bind(Border.BackgroundProperty, iconBox.GetResourceObservable("InfoBgBrush"));
        ((StrokeIcon)iconBox.Child).Bind(StrokeIcon.ForegroundProperty, iconBox.GetResourceObservable("InfoBrush"));

        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Classes = { "lbl" }, FontSize = 17, Text = title },
                new TextBlock { Classes = { "sub" }, FontSize = 14, Text = sub, TextWrapping = TextWrapping.Wrap },
            },
        };
        pill.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new StrokeIcon { Data = CheckGlyph, Size = 14, StrokeThickness = 2.2, VerticalAlignment = VerticalAlignment.Center },
                pillText,
            },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 14 };
        grid.Children.Add(radio);
        Grid.SetColumn(iconBox, 1);
        grid.Children.Add(iconBox);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        Grid.SetColumn(pill, 3);
        grid.Children.Add(pill);
        button.Content = grid;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        return new CardParts(ring, dot);
    }

    private static StackPanel LabeledColumn(string caption, Control control) => new()
    {
        Spacing = 6,
        Children = { new TextBlock { Classes = { "lbl" }, Text = caption }, control },
    };

    /// <summary>A help tile: warn-tinted icon, title, one line, and an optional menu path as chips.</summary>
    private static Border HelpTile(string glyph, string title, string text, params string[] path)
    {
        var iconBox = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(12),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new StrokeIcon { Data = glyph, Size = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        iconBox.Bind(Border.BackgroundProperty, iconBox.GetResourceObservable("WarnBgBrush"));
        ((StrokeIcon)iconBox.Child).Bind(StrokeIcon.ForegroundProperty, iconBox.GetResourceObservable("WarnBrush"));

        var mut = new TextBlock { Classes = { "mut" }, FontSize = 14, Text = text, TextWrapping = TextWrapping.Wrap };
        var col = new StackPanel
        {
            Spacing = 8,
            Children = { new StackPanel { Children = { new TextBlock { Classes = { "lbl" }, Text = title, TextWrapping = TextWrapping.Wrap }, mut } } },
        };
        if (path.Length > 0)
        {
            var chips = new WrapPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < path.Length; i++)
            {
                if (i > 0)
                {
                    var arrow = new StrokeIcon { Data = ArrowGlyph, Size = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
                    arrow.Bind(StrokeIcon.ForegroundProperty, arrow.GetResourceObservable("Text3Brush"));
                    chips.Children.Add(arrow);
                }
                chips.Children.Add(Chip(path[i]));
            }
            col.Children.Add(chips);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14 };
        grid.Children.Add(iconBox);
        Grid.SetColumn(col, 1);
        grid.Children.Add(col);
        var tile = new Border
        {
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
        tile.Bind(Border.BackgroundProperty, tile.GetResourceObservable("Card2Brush"));
        tile.Bind(Border.BorderBrushProperty, tile.GetResourceObservable("BorderBrush"));
        return tile;
    }

    private static Border Chip(string text)
    {
        var chip = new Border
        {
            Padding = new Thickness(10, 2),
            Margin = new Thickness(0, 2),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = text, FontSize = 14 },
        };
        chip.Bind(Border.BackgroundProperty, chip.GetResourceObservable("Card2Brush"));
        chip.Bind(Border.BorderBrushProperty, chip.GetResourceObservable("Border2Brush"));
        return chip;
    }

    private static Button IconButton(string text, string glyph, string classes, Action action)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new StrokeIcon { Data = glyph, Size = 16, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Left };
        button.Classes.AddRange(classes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        button.Click += (_, _) => Safe(action);
        return button;
    }
}
