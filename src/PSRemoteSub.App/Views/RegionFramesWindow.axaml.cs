using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;
using PixelRect = PSRemoteSub.Core.Imaging.PixelRect;

namespace PSRemoteSub.App.Views;

/// <summary>
/// Click-through debug outlines of every zone's OCR (green), caption (orange) and trigger (purple) boxes, laid over the Remote Play client
/// area. Position in physical pixels, size in DIP of the screen under the client area. UI thread only.
/// </summary>
public partial class RegionFramesWindow : Window
{
    public RegionFramesWindow()
    {
        InitializeComponent();
        Opened += (_, _) => OverlayPlatform?.ConfigureOverlay(TryGetPlatformHandle()?.Handle ?? 0);
    }

    /// <summary>Makes the window click-through, non-activating and absent from Alt+Tab once it has a handle.</summary>
    public IOverlayWindowPlatform? OverlayPlatform { get; init; }

    /// <summary>The key caps of the hotkey that turns the frames off, for the legend (none: no hint).</summary>
    public Func<IReadOnlyList<string>>? KeyCaps { get; init; }

    private string _legendCaps = "\0";

    /// <summary>Shows the outlines of every zone's boxes.</summary>
    public void ShowFrames(PixelRect clientScreen, IReadOnlyList<ZoneFrames> zones)
    {
        Frames.Zones = zones;
        RefreshLegend();

        var center = new PixelPoint(clientScreen.X + clientScreen.Width / 2, clientScreen.Y + clientScreen.Height / 2);
        var screen = Screens.ScreenFromPoint(center) ?? Screens.Primary;
        double scaling = screen?.Scaling ?? RenderScaling;
        // Position first: crossing to a monitor with another DPI rescales the window (see OverlayWindow).
        Position = new PixelPoint(clientScreen.X, clientScreen.Y);
        Width = (clientScreen.Width + 0.5) / scaling;
        Height = (clientScreen.Height + 0.5) / scaling;
        PlaceLegend(zones, clientScreen.Width / scaling, clientScreen.Height / scaling);

        if (!IsVisible)
            Show();
    }

    // Rebuilt only when the hotkey changes; a control is never reused across renders.
    private void RefreshLegend()
    {
        var caps = KeyCaps?.Invoke() ?? [];
        string key = string.Join("+", caps);
        if (key == _legendCaps)
            return;
        _legendCaps = key;
        _legendFull = BuildLegend(caps, compact: false);
        _legendCompact = BuildLegend(caps, compact: true);
        _legendPlacedFor = null;
    }

    private Control? _legendFull, _legendCompact;
    private string? _legendPlacedFor;

    /// <summary>
    /// Puts the legend in the corner no frame (or its label) reaches; the full card only on a big client area, else a title line;
    /// hidden when even that would cover a frame.
    /// </summary>
    private void PlaceLegend(IReadOnlyList<ZoneFrames> zones, double width, double height)
    {
        var boxes = new List<Rect>();
        foreach (var z in zones)
            foreach (var f in new[] { z.Ocr, z.Caption, z.Trigger })
                if (f is not null)
                    boxes.Add(new Rect(f.X * width, f.Y * height, f.Width * width, f.Height * height).Inflate(new Thickness(2, 30, 2, 8)));
        string signature = $"{_legendCaps}|{width:F0}x{height:F0}|{string.Join(";", boxes.Select(b => b.ToString()))}";
        if (signature == _legendPlacedFor)
            return;
        _legendPlacedFor = signature;
        const double margin = 24;
        var variants = width >= 900 && height >= 500 ? new[] { _legendFull, _legendCompact } : new[] { _legendCompact };
        foreach (var variant in variants)
        {
            if (variant is null)
                continue;
            Legend.Child = variant;
            Legend.Measure(Size.Infinity);
            var size = Legend.DesiredSize;
            // Corners, top left first.
            (HorizontalAlignment h, VerticalAlignment v, double x, double y)[] corners =
            [
                (HorizontalAlignment.Left, VerticalAlignment.Top, margin, margin),
                (HorizontalAlignment.Right, VerticalAlignment.Top, width - size.Width - margin, margin),
                (HorizontalAlignment.Left, VerticalAlignment.Bottom, margin, height - size.Height - margin),
                (HorizontalAlignment.Right, VerticalAlignment.Bottom, width - size.Width - margin, height - size.Height - margin),
            ];
            foreach (var c in corners)
            {
                var rect = new Rect(c.x, c.y, size.Width, size.Height);
                if (rect.X < 0 || rect.Y < 0 || boxes.Any(b => b.Intersects(rect)))
                    continue;
                Legend.HorizontalAlignment = c.h;
                Legend.VerticalAlignment = c.v;
                Legend.IsVisible = true;
                return;
            }
        }
        Legend.IsVisible = false;
    }

    private static readonly IBrush LegendText = new SolidColorBrush(Color.Parse("#F2F4F7"));
    private static readonly IBrush LegendMuted = new SolidColorBrush(Color.Parse("#B4B9C3"));

    private static Control BuildLegend(IReadOnlyList<string> caps, bool compact)
    {
        var panel = new StackPanel { Spacing = 10 };
        if (!compact)
            panel.Width = 340;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(RegionSelectionCanvas.OcrColor), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = Loc.Get("FrameLegendTitle"), FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = LegendText });
        panel.Children.Add(head);
        if (compact)
            return Card(panel, 12, 8);
        panel.Children.Add(new TextBlock { Text = Loc.Get("FrameLegendIntro"), FontSize = 12, Foreground = LegendMuted, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Entry(RegionSelectionCanvas.OcrColor, null, "FrameLabelOcr", "FrameLegendOcrHint"));
        panel.Children.Add(Entry(RegionSelectionCanvas.CaptionColor, DashStyle.Dash, "FrameLabelCaption", "FrameLegendCaptionHint"));
        panel.Children.Add(Entry(RegionSelectionCanvas.TriggerColor, DashStyle.Dot, "FrameLabelTrigger", "FrameLegendTriggerHint"));
        if (caps.Count > 0)
        {
            var off = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
            off.Children.Add(new TextBlock { Text = Loc.Get("FrameLegendOff"), FontSize = 12, Foreground = LegendMuted, VerticalAlignment = VerticalAlignment.Center });
            foreach (var cap in caps)
                off.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#2A2E36")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#4A505C")),
                    BorderThickness = new Thickness(1, 1, 1, 2),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(6, 1),
                    Child = new TextBlock { Text = cap, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = LegendMuted },
                });
            panel.Children.Add(off);
        }
        return Card(panel, 18, 16);
    }

    private static Border Card(Control panel, double padX, double padY) =>
        new()
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x14, 0x16, 0x1B)),
            BorderBrush = new SolidColorBrush(Color.Parse("#3B404A")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(padX, padY),
            Child = panel,
        };

    private static Control Entry(Color color, IDashStyle? dash, string label, string hint)
    {
        var swatch = new Border
        {
            Width = 30, Height = 18, VerticalAlignment = VerticalAlignment.Center,
            Child = new Avalonia.Controls.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(color), StrokeThickness = 3, StrokeDashArray = dash?.Dashes is { } d ? [.. d] : null,
                RadiusX = 3, RadiusY = 3,
            },
        };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = Loc.Get(label), FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = LegendText });
        text.Children.Add(new TextBlock { Text = Loc.Get(hint), FontSize = 12, Foreground = LegendMuted });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(swatch);
        row.Children.Add(text);
        return row;
    }
}

/// <summary>One zone's boxes for the debug frames; a null box is not drawn.</summary>
public sealed record ZoneFrames(string Name, RegionFraction? Ocr, RegionFraction? Caption, RegionFraction? Trigger, bool TriggerActive);

/// <summary>Draws every zone's outlines, as fractions of its bounds, each with its label; a trigger box is solid while seen.</summary>
public sealed class RegionFramesCanvas : Control
{
    private static readonly IImmutableSolidColorBrush OcrBrush = new ImmutableSolidColorBrush(RegionSelectionCanvas.OcrColor);
    private static readonly IImmutableSolidColorBrush CaptionBrush = new ImmutableSolidColorBrush(RegionSelectionCanvas.CaptionColor);
    private static readonly IImmutableSolidColorBrush TriggerBrush = new ImmutableSolidColorBrush(RegionSelectionCanvas.TriggerColor);

    private IReadOnlyList<ZoneFrames> _zones = [];

    public RegionFramesCanvas() => IsHitTestVisible = false;

    public IReadOnlyList<ZoneFrames> Zones
    {
        get => _zones;
        set { _zones = value; InvalidateVisual(); }
    }

    public override void Render(DrawingContext context)
    {
        foreach (var z in _zones)
        {
            // The zone's name tells the zones apart only when there are several.
            string prefix = _zones.Count > 1 ? z.Name + " · " : "";
            Draw(context, z.Ocr, OcrBrush, prefix + Loc.Get("FrameLabelOcr"), null);
            Draw(context, z.Caption, CaptionBrush, prefix + Loc.Get("FrameLabelCaption"), DashStyle.Dash);
            Draw(context, z.Trigger, TriggerBrush, prefix + Loc.Get("FrameLabelTrigger"), z.TriggerActive ? null : DashStyle.Dot);
        }
    }

    private void Draw(DrawingContext context, RegionFraction? f, IImmutableSolidColorBrush brush, string label, IDashStyle? dash)
    {
        if (f is null)
            return;
        var r = new Rect(f.X * Bounds.Width, f.Y * Bounds.Height, f.Width * Bounds.Width, f.Height * Bounds.Height);
        if (r.Width <= 0 || r.Height <= 0)
            return;
        var pen = new ImmutablePen(brush, 2, dash is null ? null : new ImmutableDashStyle(dash.Dashes, 0));
        context.DrawRectangle(null, pen, r.Deflate(1));
        RegionSelectionCanvas.DrawLabel(context, label, brush, r);
    }
}
