using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using PSRemoteSub.Core.Input;
using PSRemoteSub.Core.Pipeline;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App.Views.Pages;

/// <summary>
/// "ตัวอักษรบนจอ" (mockup New-Settings-Display): a live preview of the subtitle line, the font family, size and
/// background opacity of the active game, the speaker-colour switch and the overlay on/off switch with its hotkey.
/// Font/size/opacity ranges and validation are the old window's profiles tab. UI thread only.
/// </summary>
public sealed class DisplayPage : SettingsPage
{
    private const double MinFontSize = 16;
    private const double MaxFontSize = 64;

    /// <summary>The overlay font size is for a game screen (about 1080p high); the preview card is about 800 wide.</summary>
    private const double PreviewScale = 0.7;

    private const double NarrowWidth = 640;

    private readonly Border _gamePill = new() { Classes = { "pill", "info" } };
    private readonly TextBlock _gamePillText = new();

    // Preview
    private readonly Border _captionBox = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 4) };
    private readonly TextBlock _caption = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = Brushes.White };
    private readonly Run _captionName = new();
    private readonly Run _captionLine = new();

    // Editors
    private readonly ComboBox _font = new() { Classes = { "sel" }, MaxDropDownHeight = 320 };
    private readonly Slider _size = new() { Minimum = MinFontSize, Maximum = MaxFontSize, SmallChange = 1, LargeChange = 4, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _sizeText = new();
    private readonly Slider _opacity = new() { Minimum = 0, Maximum = 1, SmallChange = 0.05, LargeChange = 0.1, TickFrequency = 0.05, IsSnapToTickEnabled = true };
    private readonly TextBlock _opacityText = new();
    private readonly ToggleSwitch _speakerColors = new() { Classes = { "tg" } };
    private readonly ToggleSwitch _overlay = new() { Classes = { "tg" } };
    private readonly StackPanel _caps = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _capsHint = new() { Classes = { "sub" }, Text = Loc.Get("DisplayOverlayHint"), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };

    private readonly Grid _editors;
    private readonly Control[] _cells;
    private bool? _narrow;
    private List<string> _fonts = [];
    private string _capsShown = "\0";

    public DisplayPage(AppHost host) : base(host)
    {
        try
        {
            _fonts = [.. FontManager.Current.SystemFonts.Select(f => f.Name).Distinct()
                .Order(StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception)
        {
            _fonts = [];
        }
        _font.ItemsSource = _fonts;

        _font.SelectionChanged += (_, _) =>
        {
            Safe(UpdatePreview);
            EditActive(p =>
            {
                if (_font.SelectedItem is string font && !string.IsNullOrWhiteSpace(font))
                    p.FontFamily = font;
            });
        };
        _size.ValueChanged += (_, _) =>
        {
            Safe(() => _sizeText.Text = FormatSize(_size.Value));
            Safe(UpdatePreview);
            EditActive(p => p.FontSize = Math.Clamp(Math.Round(_size.Value), MinFontSize, MaxFontSize));
        };
        _opacity.ValueChanged += (_, _) =>
        {
            Safe(() => _opacityText.Text = FormatPercent(_opacity.Value));
            Safe(UpdatePreview);
            EditActive(p => p.BackgroundOpacity = Math.Round(Math.Clamp(_opacity.Value, 0, 1), 2));
        };
        _speakerColors.IsCheckedChanged += (_, _) =>
        {
            Safe(UpdatePreview);
            EditActive(p => p.SpeakerColors = _speakerColors.IsChecked == true);
        };
        _overlay.IsCheckedChanged += (_, _) => Safe(() =>
        {
            if (Loading)
                return;
            Host.SetOverlayEnabled(_overlay.IsChecked == true);
        });

        // ---- header
        var gameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        gameRow.Children.Add(_gamePillText);
        _gamePill.Child = gameRow;
        _gamePill.VerticalAlignment = VerticalAlignment.Bottom;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        header.Children.Add(PageHeader(Loc.Get("NavDisplay"), Loc.Get("DisplaySubtitle")));
        Grid.SetColumn(_gamePill, 1);
        header.Children.Add(_gamePill);

        // ---- editors card (2 columns, 1 when narrow)
        var fontCell = Cell(new TextBlock { Classes = { "lbl" }, Text = Loc.Get("DisplayFontLabel") }, _font);
        var sizeCell = Cell(LabelWithValue(Loc.Get("FontSizeLabel"), _sizeText),
            SliderRow(Small("ก", 12), _size, Small("ก", 20)));
        var opacityCell = Cell(LabelWithValue(Loc.Get("DisplayOpacityLabel"), _opacityText),
            SliderRow(Small(Loc.Get("DisplayThin"), 13), _opacity, Small(Loc.Get("DisplayThick"), 13)));
        var speakerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        speakerRow.Children.Add(_speakerColors);
        speakerRow.Children.Add(new TextBlock { Text = Loc.Get("DisplaySpeakerColors"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var speakerCell = Cell(new TextBlock { Classes = { "lbl" }, Text = Loc.Get("DisplaySpeakerLabel") }, speakerRow);
        _cells = [fontCell, sizeCell, opacityCell, speakerCell];

        _editors = new Grid { ColumnSpacing = 32, RowSpacing = 22 };
        foreach (var c in _cells)
            _editors.Children.Add(c);
        ApplyLayout(narrow: false);
        var editorsCard = new Border { Classes = { "card" }, Padding = new Thickness(22, 20), Child = _editors };

        // ---- overlay on/off row
        var overlayLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        overlayLeft.Children.Add(_overlay);
        overlayLeft.Children.Add(new TextBlock { Classes = { "lbl" }, Text = Loc.Get("DisplayOverlayShow"), VerticalAlignment = VerticalAlignment.Center });
        var overlayRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        overlayRight.Children.Add(_capsHint);
        overlayRight.Children.Add(_caps);
        var overlayRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16, Margin = new Thickness(4, 0) };
        overlayRow.Children.Add(overlayLeft);
        overlayRight.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(overlayRight, 1);
        overlayRow.Children.Add(overlayRight);

        var body = PageBody(header, BuildPreview(), editorsCard, overlayRow);
        body.MaxWidth = 860;
        body.HorizontalAlignment = HorizontalAlignment.Left;
        SetBody(body);
        SizeChanged += (_, e) => Safe(() => ApplyLayout(e.NewSize.Width - 64 < NarrowWidth));
    }

    // ---- page hooks -----------------------------------------------------------------------------------------------

    public override void OnShown() => Safe(Load);

    /// <summary>The tray, the hotkey or Home may toggle the overlay; the game may change from elsewhere.</summary>
    protected override void OnHostStateChangedCore()
    {
        bool was = Loading;
        Loading = true;
        try
        {
            _overlay.IsChecked = Settings.OverlayEnabled;
            ShowCaps();
        }
        finally
        {
            Loading = was;
        }
    }

    // ---- loading --------------------------------------------------------------------------------------------------

    private void Load()
    {
        var p = Settings.GetActive();
        bool was = Loading;
        Loading = true;
        try
        {
            _gamePillText.Text = Loc.Format("DisplayUsingGame", p.Name);
            if (!string.IsNullOrWhiteSpace(p.FontFamily) && !_fonts.Contains(p.FontFamily))
            {
                _fonts = [p.FontFamily, .. _fonts];
                _font.ItemsSource = _fonts;
            }
            _font.SelectedItem = p.FontFamily;
            _size.Value = Math.Clamp(p.FontSize, MinFontSize, MaxFontSize);
            _sizeText.Text = FormatSize(_size.Value);
            _opacity.Value = Math.Clamp(p.BackgroundOpacity, 0, 1);
            _opacityText.Text = FormatPercent(_opacity.Value);
            _speakerColors.IsChecked = p.SpeakerColors;
            _overlay.IsChecked = Settings.OverlayEnabled;
            ShowCaps();
        }
        finally
        {
            Loading = was;
        }
        UpdatePreview();
    }

    private void ShowCaps()
    {
        var caps = HotkeyLabels.KeyCaps(Settings.Hotkeys.GetValueOrDefault(HotkeyAction.ToggleOverlay), Host.Platform.Kind);
        string key = string.Join("+", caps);
        if (key == _capsShown)
            return;
        _capsShown = key;
        _caps.Children.Clear();
        foreach (var cap in caps)
            _caps.Children.Add(new Border { Classes = { "kbd" }, Child = new TextBlock { Text = cap } });
        _capsHint.IsVisible = caps.Count > 0;
    }

    // ---- preview --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Mimics OverlayWindow's line: a box of black at the chosen opacity, white text with a drop shadow, the name in
    /// the speaker's colour (white when the switch is off). The game's font size is scaled by <see cref="PreviewScale"/>.
    /// </summary>
    private void UpdatePreview()
    {
        string family = _font.SelectedItem as string ?? "Leelawadee UI";
        try
        {
            _caption.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(family) ? "Leelawadee UI" : family);
        }
        catch (Exception)
        {
            // keep the previous font
        }
        _caption.FontSize = Math.Clamp(_size.Value, MinFontSize, MaxFontSize) * PreviewScale;
        _captionBox.Background = new SolidColorBrush(Colors.Black, Math.Clamp(_opacity.Value, 0, 1));
        string name = Loc.Get("DisplaySampleName");
        _captionName.Text = name + ": ";
        _captionName.Foreground = _speakerColors.IsChecked == true && Color.TryParse(SpeakerPalette.For(name), out var c)
            ? new SolidColorBrush(c)
            : Brushes.White;
        _captionLine.Text = Loc.Get("DisplaySampleLine");
    }

    private Control BuildPreview()
    {
        static Border Block(double left, double top, double w, double h, string fill, double radius = 0) => new()
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(left, top, 0, 0), Width = w, Height = h,
            Background = new SolidColorBrush(Color.Parse(fill)),
            CornerRadius = new CornerRadius(radius, radius, 3, 3),
        };

        // The scene: sky above, ground below, a few dark buildings and two figures standing on the horizon.
        const double H = 240;
        double horizon = H * 156 / 300;
        var scene = new Grid { Height = H };
        scene.Children.Add(new Border { VerticalAlignment = VerticalAlignment.Top, Height = horizon, Background = new SolidColorBrush(Color.Parse("#22344a")) });
        scene.Children.Add(new Border { VerticalAlignment = VerticalAlignment.Bottom, Height = H - horizon, Background = new SolidColorBrush(Color.Parse("#23281f")) });
        scene.Children.Add(Block(93, horizon - 58, 109, 58, "#1d2630"));
        scene.Children.Add(Block(581, horizon - 76, 143, 76, "#18212a"));
        scene.Children.Add(Block(236, horizon - 50, 76, 50, "#202c38"));
        scene.Children.Add(Block(362, horizon - 38, 50, 49, "#0f141a", 25));
        scene.Children.Add(Block(454, horizon - 30, 42, 42, "#141a20", 21));
        scene.Children.Add(new Border { Background = new SolidColorBrush(Colors.Black, 0.15) });

        _caption.Inlines ??= [];
        _caption.Inlines.Add(_captionName);
        _caption.Inlines.Add(_captionLine);
        _caption.Effect = new DropShadowEffect { BlurRadius = 4, OffsetX = 1, OffsetY = 1, Color = Colors.Black, Opacity = 1 };
        _captionBox.Child = _caption;
        _captionBox.HorizontalAlignment = HorizontalAlignment.Center;
        _captionBox.VerticalAlignment = VerticalAlignment.Bottom;
        _captionBox.Margin = new Thickness(20, 0, 20, 22);
        scene.Children.Add(_captionBox);

        var badge = new Border
        {
            Classes = { "pill" }, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12), Background = new SolidColorBrush(Colors.Black, 0.6),
            Child = new TextBlock { Text = Loc.Get("DisplayPreviewBadge"), Foreground = Brushes.White },
        };
        scene.Children.Add(badge);

        return new Border
        {
            CornerRadius = new CornerRadius(12), ClipToBounds = true, BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse("#363b44")), Child = scene,
        };
    }

    // ---- layout ---------------------------------------------------------------------------------------------------

    private void ApplyLayout(bool narrow)
    {
        if (_narrow == narrow)
            return;
        _narrow = narrow;
        _editors.ColumnDefinitions.Clear();
        _editors.RowDefinitions.Clear();
        _editors.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        if (!narrow)
            _editors.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        int rows = narrow ? _cells.Length : (_cells.Length + 1) / 2;
        for (int i = 0; i < rows; i++)
            _editors.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int i = 0; i < _cells.Length; i++)
        {
            Grid.SetRow(_cells[i], narrow ? i : i / 2);
            Grid.SetColumn(_cells[i], narrow ? 0 : i % 2);
        }
    }

    private static StackPanel Cell(Control label, Control editor) => new() { Spacing = 8, Children = { label, editor } };

    private static Grid LabelWithValue(string title, TextBlock value)
    {
        var pill = new Border { Classes = { "pill", "info" }, Child = value, HorizontalAlignment = HorizontalAlignment.Right };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new TextBlock { Classes = { "lbl" }, Text = title, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(pill, 1);
        row.Children.Add(pill);
        return row;
    }

    private static Grid SliderRow(Control left, Slider slider, Control right)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*,32"), ColumnSpacing = 12 };
        left.HorizontalAlignment = HorizontalAlignment.Left;
        right.HorizontalAlignment = HorizontalAlignment.Right;
        slider.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(left);
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        Grid.SetColumn(right, 2);
        row.Children.Add(right);
        return row;
    }

    private static TextBlock Small(string text, double size) =>
        new() { Classes = { "sub" }, Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center };

    private static string FormatSize(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);

    private static string FormatPercent(double v) =>
        (Math.Clamp(v, 0, 1) * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
