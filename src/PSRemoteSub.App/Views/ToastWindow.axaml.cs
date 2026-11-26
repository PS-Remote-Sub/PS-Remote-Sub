using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using PSRemoteSub.App.Views.Controls;
using PSRemoteSub.Core.Notices;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.App.Views;

/// <summary>
/// One notice on screen (New-Toast; Avalonia's TrayIcon has no balloons, Ruling R10): a topmost window that is shown
/// without taking focus from the game but takes clicks on its buttons. Done notices count down (6 s, paused while the
/// pointer is over the card) and then raise <see cref="Expired"/>; the others stay until closed or replaced.
/// Placed and stacked by <see cref="Notifier"/>. UI thread only.
/// </summary>
public partial class ToastWindow : Window
{
    /// <summary>The card's width (the window's).</summary>
    public const double CardWidth = 400;

    // Stroke icons from the mockup (24×24 grid).
    private const string DoneGlyph = "M5 12.5l4.5 4.5L19 7.5";
    private const string WarnGlyph = "M12 3.5 2.5 20h19L12 3.5zM12 10v4.5M12 17.3h.01";
    private const string InfoGlyph = "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM12 11v5M12 7.7h.01";
    private const string UpdateGlyph = "M12 4v11M7 10l5 5 5-5M5 20h14";
    private const string CloseGlyph = "M6 6l12 12M18 6 6 18";

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private TimeSpan _remaining;

    public ToastWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = Tick };
        _timer.Tick += (_, _) => CountDown();
        CloseButton.Content = new StrokeIcon { Data = CloseGlyph, Size = 18 };
        ToolTip.SetTip(CloseButton, Loc.Get("ToastClose"));
        Avalonia.Automation.AutomationProperties.SetName(CloseButton, Loc.Get("ToastClose"));
        CloseButton.Click += (_, _) => CloseClicked?.Invoke(this, EventArgs.Empty);
        Opened += (_, _) => OverlayPlatform?.ConfigureToast(TryGetPlatformHandle()?.Handle ?? 0);
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Keeps the window topmost and non-activating (but clickable) once it has a handle.</summary>
    public IOverlayWindowPlatform? OverlayPlatform { get; init; }

    /// <summary>The notice shown, once <see cref="Present"/> ran.</summary>
    public Notice? Notice { get; private set; }

    /// <summary>A button was clicked: its action id.</summary>
    public event EventHandler<string>? ActionClicked;

    /// <summary>The close × was clicked.</summary>
    public event EventHandler? CloseClicked;

    /// <summary>A Done notice's countdown ran out.</summary>
    public event EventHandler? Expired;

    /// <summary>Fills the card from <paramref name="notice"/>. Call once, before the window is shown.</summary>
    public void Present(Notice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        Notice = notice;
        var (fg, bg, glyph) = notice.Kind switch
        {
            NoticeKind.Done => ("OkBrush", "OkBgBrush", DoneGlyph),
            NoticeKind.Warn => ("WarnBrush", "WarnBgBrush", WarnGlyph),
            _ => ("InfoBrush", "InfoBgBrush", notice.Topic == Notifier.UpdateTopic ? UpdateGlyph : InfoGlyph),
        };
        IconTile.Bind(Border.BackgroundProperty, IconTile.GetResourceObservable(bg));
        var icon = new StrokeIcon
        {
            Data = glyph,
            Size = 20,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        icon.Bind(StrokeIcon.ForegroundProperty, IconTile.GetResourceObservable(fg));
        IconTile.Child = icon;

        TitleText.Text = notice.Title;
        BodyText.Text = notice.Body;
        BodyText.IsVisible = !string.IsNullOrEmpty(notice.Body);

        for (int i = 0; i < notice.Actions.Count; i++)
        {
            var action = notice.Actions[i];
            // The first button is the fix (primary on a warning, plain on info, as in the mockup); later ones are quiet.
            var button = new Button { Content = action.Label, Classes = { "btn", "sm" } };
            if (i == 0 && notice.Kind == NoticeKind.Warn)
                button.Classes.Add("primary");
            else if (i > 0)
                button.Classes.Add("ghost");
            button.Click += (_, _) => ActionClicked?.Invoke(this, action.Id);
            Buttons.Children.Add(button);
        }
        Buttons.IsVisible = notice.Actions.Count > 0;

        CountdownTrack.IsVisible = notice.AutoDismiss;
        if (notice.AutoDismiss)
        {
            _remaining = Notice.AutoDismissAfter;
            CountdownFill.Width = CardWidth; // full until the first tick (the track has no size before layout)
            _clock.Restart();
            _timer.Start();
        }
    }

    /// <summary>The card's height (DIPs) at <see cref="CardWidth"/>, for placing the window before it is shown.</summary>
    public double MeasureHeight()
    {
        Card.Measure(new Size(CardWidth, double.PositiveInfinity));
        return Card.DesiredSize.Height;
    }

    private void CountDown()
    {
        var elapsed = _clock.Elapsed;
        _clock.Restart();
        if (!IsPointerOver)
            _remaining -= elapsed;
        double fraction = Math.Clamp(_remaining / Notice.AutoDismissAfter, 0, 1);
        CountdownFill.Width = CountdownTrack.Bounds.Width * fraction;
        if (_remaining > TimeSpan.Zero)
            return;
        _timer.Stop();
        Expired?.Invoke(this, EventArgs.Empty);
    }
}
