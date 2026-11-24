using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Layout;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;
using PixelRect = PSRemoteSub.Core.Imaging.PixelRect;

namespace PSRemoteSub.App.Views;

/// <summary>
/// Click-through subtitle box placed over (or above) the subtitle region of the Remote Play window.
/// Positions are physical pixels; sizes are converted to DIP with the scaling of the screen under the region.
/// UI thread only.
/// </summary>
public partial class OverlayWindow : Window
{
    /// <summary>Text opacity while the AI refinement of the shown line is pending.</summary>
    private const double ProvisionalOpacity = 0.7;

    public OverlayWindow()
    {
        InitializeComponent();
        Opened += (_, _) => OverlayPlatform?.ConfigureOverlay(TryGetPlatformHandle()?.Handle ?? 0);
    }

    /// <summary>Makes the window click-through, non-activating and absent from Alt+Tab once it has a handle.</summary>
    public IOverlayWindowPlatform? OverlayPlatform { get; init; }

    /// <summary>Extra width for a Fit box sized to its text, so it never re-wraps on rounding.</summary>
    private const int FitSlackPx = 2;

    /// <summary>The text of each caption row, for the font size and the provisional opacity.</summary>
    private readonly List<TextBlock> _captions = [];
    private double _textOpacity = 1.0;

    /// <summary>
    /// One box per text line, like the game draws each speaker's line on its own background with a gap between them.
    /// A line with a speaker starts with <c>Name: </c> in the speaker's colour (white without one), then the line in
    /// the caption's own colour. Returns the rows, in the order of the boxes.
    /// </summary>
    private IReadOnlyList<CaptionRow> SetRows(string text, IReadOnlyList<SpeakerTag?>? speakers,
        IReadOnlyList<RegionFraction>? rowBounds, IBrush background, FontFamily font)
    {
        Lines.Children.Clear();
        _captions.Clear();
        // Stacked and centred; the Fit layout over the game's lines places the boxes itself.
        Lines.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        Lines.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        var rows = CaptionRows.Split(text, speakers, rowBounds);
        foreach (var row in rows)
        {
            var block = new TextBlock
            {
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                FontFamily = font,
                Opacity = _textOpacity,
                Effect = new DropShadowEffect { BlurRadius = 4, OffsetX = 1, OffsetY = 1, Color = Colors.Black, Opacity = 1 },
            };
            if (row.Speaker is { } speaker)
            {
                var inlines = block.Inlines ??= [];
                inlines.Add(new Run(speaker.Name + ": ") { Foreground = NameBrush(speaker.Color) });
                inlines.Add(new Run(row.Text));
            }
            else
            {
                block.Text = row.Text;
            }
            _captions.Add(block);
            Lines.Children.Add(new Border
            {
                Background = background,
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Child = block,
            });
        }
        return rows;
    }

    /// <summary>
    /// Fit with each row's game line known: each box covers its game line (growing if its text needs more room), with
    /// the gap kept between boxes. Call after <see cref="MeasureAt"/>. Returns the window rectangle (screen pixels).
    /// </summary>
    private PixelRect PlaceOverGameLines(IReadOnlyList<CaptionRow> rows, PixelRect clientScreen, double scaling)
    {
        var boxes = Lines.Children.OfType<Border>().ToList();
        var english = rows.Select(r => ToScreen(clientScreen, r.Cover!)).ToList();
        // As measured at the OCR box's width; a little slack so the text never re-wraps on rounding.
        var content = boxes.Select(b => ((int)Math.Ceiling(b.DesiredSize.Width * scaling) + FitSlackPx,
            (int)Math.Ceiling(b.DesiredSize.Height * scaling))).ToList();
        int gapPx = (int)Math.Round(Lines.Spacing * scaling);
        var placed = OverlayLayout.ComputeFitRows(clientScreen, english, content, gapPx);

        int x0 = placed.Min(p => p.X), y0 = placed.Min(p => p.Y);
        int x1 = placed.Max(p => p.Right), y1 = placed.Max(p => p.Bottom);
        Lines.Spacing = 0;
        Lines.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        Lines.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        int above = y0;
        for (int i = 0; i < boxes.Count; i++)
        {
            var p = placed[i];
            boxes[i].HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            boxes[i].Width = p.Width / scaling;
            boxes[i].Height = p.Height / scaling;
            boxes[i].Margin = new Thickness((p.X - x0) / scaling, (p.Y - above) / scaling, 0, 0);
            above = p.Bottom;
        }
        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }

    private static IBrush NameBrush(string? color) =>
        color is not null && Color.TryParse(color, out var c) ? new SolidColorBrush(c) : Brushes.White;

    /// <summary>Dims the text while it is a provisional line that the AI may still replace.</summary>
    public void SetProvisional(bool provisional)
    {
        _textOpacity = provisional ? ProvisionalOpacity : 1.0;
        foreach (var caption in _captions)
            caption.Opacity = _textOpacity;
    }

    /// <summary>Shows <paramref name="text"/> styled by <paramref name="profile"/> over a client area.</summary>
    /// <param name="placement">The zone's display mode (Block = Custom, Bubbles = Fit).</param>
    /// <param name="clientScreen">Remote Play client area in physical screen pixels.</param>
    /// <param name="ocrRegion">The OCR box, which caps the width of a Fit box; null when none is set.</param>
    /// <param name="captionBox">The caption box: where the line goes, except for Fit with known text bounds.</param>
    /// <param name="textBounds">Where the game drew the subtitle (fraction of the client area), for the Fit placement.</param>
    /// <param name="speakers">Who says each text line: the name is drawn before it, in the game's colour for it.</param>
    /// <param name="rowBounds">Where the game drew each text line, for Fit to cover each one with its own box.</param>
    public void ShowText(string text, GameProfile profile, OverlayPlacement placement, PixelRect clientScreen,
        RegionFraction? ocrRegion, RegionFraction captionBox, RegionFraction? textBounds, IReadOnlyList<SpeakerTag?>? speakers = null,
        IReadOnlyList<RegionFraction>? rowBounds = null)
    {
        var rows = SetRows(text, speakers, rowBounds,
            new SolidColorBrush(Colors.Black, Math.Clamp(profile.BackgroundOpacity, 0, 1)),
            new FontFamily(string.IsNullOrWhiteSpace(profile.FontFamily) ? "Leelawadee UI" : profile.FontFamily));
        double fontSize = Math.Clamp(profile.FontSize, 8, 200);

        PixelRect rect;
        double scaling;
        if (placement == OverlayPlacement.Fit && textBounds is not null && ocrRegion is not null
            && rows.All(r => r.Cover is not null))
        {
            // Each box over the game's own line, hiding it; at the profile's font size, never wider than the OCR box.
            var target = ToScreen(clientScreen, ocrRegion);
            scaling = ScalingAt(target);
            MeasureAt(fontSize, target.Width / scaling);
            rect = PlaceOverGameLines(rows, clientScreen, scaling);
        }
        else if (placement == OverlayPlacement.Fit && textBounds is not null && ocrRegion is not null)
        {
            // Over the game's own subtitle, at the profile's font size, never wider than the OCR box.
            var target = ToScreen(clientScreen, ocrRegion);
            scaling = ScalingAt(target);
            var size = MeasureAt(fontSize, target.Width / scaling);
            int contentPx = (int)Math.Ceiling(size.Height * scaling);
            // The content's natural width (at most the region's), so the box can hug the game's subtitle.
            // A box exactly as wide as the text re-wraps its last word on any rounding loss (or a DPI change while
            // moving): a little slack keeps the line on the lines it was measured with.
            int widthPx = (int)Math.Ceiling(size.Width * scaling) + FitSlackPx;
            rect = OverlayLayout.ComputeFit(clientScreen, ocrRegion, textBounds, widthPx, contentPx, 0);
        }
        else
        {
            // In the caption box the user set: the font shrinks (down to 16) before the box grows upwards.
            var target = ToScreen(clientScreen, captionBox);
            scaling = ScalingAt(target);
            double widthDip = target.Width / scaling;
            double s = scaling;
            double chosen = FontFit.Choose(fontSize, target.Height,
                size => (int)Math.Ceiling(MeasureAt(size, widthDip).Height * s));
            int contentPx = (int)Math.Ceiling(MeasureAt(chosen, widthDip).Height * scaling);
            rect = OverlayLayout.ComputeCaption(clientScreen, captionBox, contentPx);
        }

        // Position first: crossing to a monitor with another DPI rescales the window, and the DIP size below
        // must be interpreted with the new scaling.
        Position = new PixelPoint(rect.X, rect.Y);
        // Avalonia's Win32 resize truncates DIP × scaling to pixels; half a pixel more lands on the intended pixel at
        // scalings like 110%, 130% or 175% instead of one short.
        Width = (Math.Max(rect.Width, 1) + 0.5) / scaling;
        Height = (Math.Max(rect.Height, 1) + 0.5) / scaling;

        if (!IsVisible)
            Show();
    }

    private static PixelRect ToScreen(PixelRect clientScreen, RegionFraction region) =>
        region.ToPixels(clientScreen.Width, clientScreen.Height).Offset(clientScreen.X, clientScreen.Y);

    /// <summary>Scaling of the screen under the centre of <paramref name="target"/> (physical pixels).</summary>
    private double ScalingAt(PixelRect target)
    {
        var center = new PixelPoint(target.X + target.Width / 2, target.Y + target.Height / 2);
        var screen = Screens.ScreenFromPoint(center) ?? Screens.Primary;
        return screen?.Scaling ?? RenderScaling;
    }

    /// <summary>
    /// Sets the captions' font size, with each row's padding and the gap between rows in proportion, and returns the
    /// panel's desired size (DIP) at a width.
    /// </summary>
    private Size MeasureAt(double fontSize, double widthDip)
    {
        var padding = new Thickness(Math.Round(fontSize * 0.4), Math.Round(fontSize * 0.15));
        foreach (var row in Lines.Children.OfType<Border>())
            row.Padding = padding;
        Lines.Spacing = Math.Round(fontSize * 0.25);
        // Changing a child's text or visibility does not invalidate Panel itself, and Measure with an unchanged size
        // on a valid control returns the old size: force it, or two quick refreshes size the box from stale content.
        foreach (var caption in _captions)
        {
            caption.FontSize = fontSize;
            caption.InvalidateMeasure();
        }
        foreach (var row in Lines.Children)
            row.InvalidateMeasure();
        Lines.InvalidateMeasure();
        Panel.InvalidateMeasure();
        Panel.Measure(new Size(widthDip, double.PositiveInfinity));
        return Panel.DesiredSize;
    }
}
