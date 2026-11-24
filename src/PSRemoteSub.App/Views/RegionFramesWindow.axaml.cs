using Avalonia;
using Avalonia.Controls;
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

    /// <summary>Shows the outlines of every zone's boxes.</summary>
    public void ShowFrames(PixelRect clientScreen, IReadOnlyList<ZoneFrames> zones)
    {
        Frames.Zones = zones;

        var center = new PixelPoint(clientScreen.X + clientScreen.Width / 2, clientScreen.Y + clientScreen.Height / 2);
        var screen = Screens.ScreenFromPoint(center) ?? Screens.Primary;
        double scaling = screen?.Scaling ?? RenderScaling;
        // Position first: crossing to a monitor with another DPI rescales the window (see OverlayWindow).
        Position = new PixelPoint(clientScreen.X, clientScreen.Y);
        Width = (clientScreen.Width + 0.5) / scaling;
        Height = (clientScreen.Height + 0.5) / scaling;

        if (!IsVisible)
            Show();
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
            Draw(context, z.Ocr, OcrBrush, $"{z.Name} · {Loc.Get("BoxLabelOcr")}", solid: true);
            Draw(context, z.Caption, CaptionBrush, $"{z.Name} · {Loc.Get("BoxLabelCaption")}", solid: true);
            Draw(context, z.Trigger, TriggerBrush, $"{z.Name} · {Loc.Get("BoxLabelTrigger")}", z.TriggerActive);
        }
    }

    private void Draw(DrawingContext context, RegionFraction? f, IImmutableSolidColorBrush brush, string label, bool solid)
    {
        if (f is null)
            return;
        var r = new Rect(f.X * Bounds.Width, f.Y * Bounds.Height, f.Width * Bounds.Width, f.Height * Bounds.Height);
        if (r.Width <= 0 || r.Height <= 0)
            return;
        var pen = new ImmutablePen(brush, 2, solid ? null : new ImmutableDashStyle([4, 3], 0));
        context.DrawRectangle(null, pen, r.Deflate(1));
        RegionSelectionCanvas.DrawLabel(context, label, brush, r);
    }
}
