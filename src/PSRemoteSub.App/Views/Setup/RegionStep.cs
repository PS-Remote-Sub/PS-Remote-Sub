using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using PSRemoteSub.App.Views.Controls;
using PSRemoteSub.Core.Input;

namespace PSRemoteSub.App.Views.Setup;

/// <summary>
/// Step 4 "บอกแอปว่าซับอยู่ตรงไหน" (mockup New-Setup-4): a sample game frame drawn with shapes (the subtitle line in a
/// dashed "ซับอยู่ตรงนี้" box), three numbered instructions, "เริ่มลากกรอบ" (<see cref="AppHost.RequestRegionSelection"/>
/// for the active game's last zone; with no game frame yet AppHost says so) and the region hotkey as key caps.
/// Nothing to apply: the picker saves the boxes itself.
/// </summary>
public sealed class RegionStep : SetupStep
{
    private const string FrameGlyph = "M4 9V4h5M15 4h5v5M20 15v5h-5M9 20H4v-5";
    private const string CursorGlyph = "M5 3l14 7-6 2-2 6z";
    private const double FrameWidth = 380;
    private const double FrameHeight = 214;

    private readonly WrapPanel _hint = new() { Orientation = Orientation.Horizontal };

    public RegionStep(AppHost host) : base(host)
    {
        var start = new Button
        {
            Classes = { "btn" },
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new StrokeIcon { Data = FrameGlyph, Size = 18, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = Loc.Get("SetupRegionStart"), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        start.Click += (_, _) => Safe(() => Host.RequestRegionSelection(null));

        var enterLine = new WrapPanel { Orientation = Orientation.Horizontal };
        enterLine.Children.Add(new TextBlock { Text = Loc.Get("SetupRegionStep3"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        enterLine.Children.Add(Kbd("Enter"));

        var steps = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Numbered(1, new TextBlock { Text = Loc.Get("SetupRegionStep1"), TextWrapping = TextWrapping.Wrap }),
                Numbered(2, new TextBlock { Text = Loc.Get("SetupRegionStep2"), TextWrapping = TextWrapping.Wrap }),
                Numbered(3, enterLine),
                new StackPanel { Spacing = 8, Margin = new Thickness(0, 6, 0, 0), Children = { start, _hint } },
            },
        };

        var frame = SampleFrame();
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 28 };
        layout.Children.Add(frame);
        Grid.SetColumn(steps, 1);
        layout.Children.Add(steps);
        Content = layout;
    }

    public override string StepName => Loc.Get("SetupStepRegion");

    public override string Title => Loc.Get("SetupRegionTitle");

    public override string Subtitle => Loc.Get("SetupRegionSub");

    /// <summary>The hotkey may have been changed in settings since the wizard opened.</summary>
    public override void OnShown()
    {
        _hint.Children.Clear();
        var caps = HotkeyLabels.KeyCaps(Settings.Hotkeys.GetValueOrDefault(HotkeyAction.SelectRegion), Host.Platform.Kind);
        _hint.IsVisible = caps.Count > 0;
        if (caps.Count == 0)
            return;
        _hint.Children.Add(new TextBlock { Classes = { "sub" }, Text = Loc.Get("SetupRegionLater"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        foreach (var cap in caps)
        {
            var kbd = Kbd(cap);
            kbd.Margin = new Thickness(0, 0, 4, 0);
            _hint.Children.Add(kbd);
        }
    }

    private static Grid Numbered(int number, Control text)
    {
        var circle = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        circle.Bind(Border.BackgroundProperty, circle.GetResourceObservable("Card2Brush"));
        circle.Bind(Border.BorderBrushProperty, circle.GetResourceObservable("Border2Brush"));
        text.Margin = new Thickness(0, 2, 0, 0);
        text.VerticalAlignment = VerticalAlignment.Top;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        row.Children.Add(circle);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    /// <summary>A dimmed sample scene (sky, ground, buildings, two figures) with a subtitle line in the dashed box.</summary>
    private static Border SampleFrame()
    {
        var canvas = new Canvas { Width = FrameWidth, Height = FrameHeight };
        void Rect(double x, double y, double w, double h, string color, double radius = 0)
        {
            var r = new Rectangle { Width = w, Height = h, Fill = new SolidColorBrush(Color.Parse(color)), RadiusX = radius, RadiusY = radius };
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            canvas.Children.Add(r);
        }
        Rect(0, 0, 380, 111, "#22344A");
        Rect(0, 111, 380, 103, "#23281F");
        Rect(44, 59, 51, 52, "#1D2630");
        Rect(273, 44, 67, 68, "#18212A");
        Rect(111, 67, 36, 44, "#202C38");
        Rect(170, 83, 24, 44, "#0F141A", 6);
        Rect(214, 89, 20, 38, "#141A20", 5);
        Rect(0, 0, 380, 214, "#73000000"); // the picker's dimming

        var line = new TextBlock
        {
            Text = "Detective: We need to find the witness before dawn.",
            Width = 156,
            FontSize = 7,
            FontFamily = new FontFamily("Arial"),
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
        };
        Canvas.SetLeft(line, 112);
        Canvas.SetTop(line, 178);
        canvas.Children.Add(line);

        var box = new Rectangle
        {
            Width = 172,
            Height = 22,
            RadiusX = 3,
            RadiusY = 3,
            StrokeThickness = 2,
            StrokeDashArray = new AvaloniaList<double> { 3, 2 },
            Fill = new SolidColorBrush(Color.Parse("#1F5AA8FF")),
        };
        box.Bind(Shape.StrokeProperty, box.GetResourceObservable("AccentBrush"));
        Canvas.SetLeft(box, 104);
        Canvas.SetTop(box, 172);
        canvas.Children.Add(box);

        var labelText = new TextBlock { Text = Loc.Get("SetupRegionHere"), FontSize = 12, FontWeight = FontWeight.SemiBold };
        labelText.Bind(TextBlock.ForegroundProperty, labelText.GetResourceObservable("OnAccentBrush"));
        var label = new Border { Padding = new Thickness(8, 1), CornerRadius = new CornerRadius(4), Child = labelText };
        label.Bind(Border.BackgroundProperty, label.GetResourceObservable("AccentBrush"));
        Canvas.SetLeft(label, 104);
        Canvas.SetTop(label, 146);
        canvas.Children.Add(label);

        var cursor = new StrokeIcon { Data = CursorGlyph, Size = 18, StrokeThickness = 1.6, Foreground = Brushes.White };
        Canvas.SetLeft(cursor, 268);
        Canvas.SetTop(cursor, 186);
        canvas.Children.Add(cursor);

        var frame = new Border
        {
            Width = FrameWidth + 2,
            Height = FrameHeight + 2,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Top,
            Child = canvas,
        };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("Border2Brush"));
        return frame;
    }
}
