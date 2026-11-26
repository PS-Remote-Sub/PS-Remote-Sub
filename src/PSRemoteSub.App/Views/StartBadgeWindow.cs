using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using PSRemoteSub.Core.Platform;
using PixelRect = PSRemoteSub.Core.Imaging.PixelRect;

namespace PSRemoteSub.App.Views;

/// <summary>
/// The small "Thai subtitles are on" badge at the top right of the game area: a click-through, non-activating, topmost window
/// set up like <see cref="OverlayWindow"/> (never the toast setup), so it can never take a click or focus from the game.
/// Hard-coded dark. UI thread only.
/// </summary>
public sealed class StartBadgeWindow : Window
{
    private const double MarginDip = 28;
    private static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(0xD1, 0x10, 0x12, 0x16));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#C4C9D2"));
    private static readonly IBrush CapFill = new SolidColorBrush(Color.Parse("#2A2E36"));
    private static readonly IBrush CapLine = new SolidColorBrush(Color.Parse("#4A505C"));
    private static readonly IBrush Dot = new SolidColorBrush(Color.Parse("#6FE09A"));

    private readonly Border _root = new()
    {
        Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(350) }],
    };

    public StartBadgeWindow()
    {
        Title = "PS Remote Sub start badge";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        IsHitTestVisible = false;
        Content = _root;
        Opened += (_, _) => OverlayPlatform?.ConfigureOverlay(TryGetPlatformHandle()?.Handle ?? 0);
    }

    /// <summary>Makes the window click-through, non-activating and absent from Alt+Tab once it has a handle.</summary>
    public IOverlayWindowPlatform? OverlayPlatform { get; init; }

    /// <summary>Shows the badge at full opacity at the top right of the client area (physical screen pixels).</summary>
    public void ShowBadge(PixelRect clientScreen, IReadOnlyList<string> keyCaps)
    {
        var center = new PixelPoint(clientScreen.X + clientScreen.Width / 2, clientScreen.Y + clientScreen.Height / 2);
        double scaling = (Screens.ScreenFromPoint(center) ?? Screens.Primary)?.Scaling ?? RenderScaling;
        // A new content every time: a control is never reused across renders.
        var content = Build(keyCaps);
        _root.Child = content;
        _root.Measure(Size.Infinity);
        var size = _root.DesiredSize;
        int widthPx = (int)Math.Ceiling(size.Width * scaling), heightPx = (int)Math.Ceiling(size.Height * scaling);
        int margin = (int)Math.Round(MarginDip * scaling);
        // Position first: crossing to a monitor with another DPI rescales the window (see OverlayWindow).
        Position = new PixelPoint(Math.Max(clientScreen.X, clientScreen.X + clientScreen.Width - widthPx - margin), clientScreen.Y + margin);
        Width = (widthPx + 0.5) / scaling;
        Height = (heightPx + 0.5) / scaling;
        _root.Opacity = 1;
        if (!IsVisible)
            Show();
    }

    /// <summary>Starts the fade-out (the window hides after it with <see cref="HideBadge"/>).</summary>
    public void FadeOut() => _root.Opacity = 0;

    /// <summary>Hides at once, no fade.</summary>
    public void HideBadge()
    {
        if (!IsVisible)
            return;
        // Without a transition frame in between, so a later Show never starts from a half-faded state.
        _root.Opacity = 0;
        Hide();
    }

    private static Control Build(IReadOnlyList<string> keyCaps)
    {
        var caps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var cap in keyCaps)
            caps.Children.Add(new Border
            {
                Background = CapFill,
                BorderBrush = CapLine,
                BorderThickness = new Thickness(1, 1, 1, 2),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 1),
                Child = new TextBlock { Text = cap, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Muted },
            });
        var hide = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (keyCaps.Count > 0)
        {
            hide.Children.Add(new TextBlock { Text = Loc.Get("StartBadgeHide"), FontSize = 14, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            hide.Children.Add(caps);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = Dot, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock
        {
            Text = Loc.Get("StartBadgeOn"), FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (keyCaps.Count > 0)
            row.Children.Add(hide);
        return new Border { Background = Fill, CornerRadius = new CornerRadius(999), Padding = new Thickness(16, 8), Child = row };
    }
}
