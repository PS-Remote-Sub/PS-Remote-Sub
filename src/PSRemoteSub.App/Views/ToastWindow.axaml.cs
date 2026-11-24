using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.App.Views;

/// <summary>
/// Stand-in for tray balloon notifications (Avalonia's TrayIcon has none; Ruling R10): a topmost, click-through,
/// non-activating card at the bottom-right of the primary screen's working area that hides itself after 4 s.
/// UI thread only.
/// </summary>
public partial class ToastWindow : Window
{
    private const double CardWidth = 360;
    private const double ScreenMargin = 12;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(4);

    private readonly DispatcherTimer _timer;

    public ToastWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = Lifetime };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Hide();
        };
        Opened += (_, _) => OverlayPlatform?.ConfigureOverlay(TryGetPlatformHandle()?.Handle ?? 0);
    }

    /// <summary>Makes the window click-through and non-activating once it has a handle.</summary>
    public IOverlayWindowPlatform? OverlayPlatform { get; init; }

    /// <summary>Shows <paramref name="message"/>, replacing any toast on screen, and restarts the 4 s timer.</summary>
    public void ShowMessage(string message)
    {
        Message.Text = message;
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
            return;

        double scaling = screen.Scaling;
        Card.Measure(new Size(CardWidth, double.PositiveInfinity));
        double height = Card.DesiredSize.Height;
        var area = screen.WorkingArea;
        int margin = (int)Math.Round(ScreenMargin * scaling);
        Position = new PixelPoint(
            area.Right - (int)Math.Ceiling(CardWidth * scaling) - margin,
            area.Bottom - (int)Math.Ceiling(height * scaling) - margin);
        Width = CardWidth;
        Height = height;

        if (!IsVisible)
            Show();
        _timer.Stop();
        _timer.Start();
    }
}
