using Avalonia;
using Avalonia.Controls;
using PSRemoteSub.App.Views.Controls;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.App.Views;

/// <summary>
/// The first-time hint that the app lives in the tray / menu bar. A topmost, non-activating window (it never takes focus
/// from a game; its two buttons still take clicks): bottom-right of the primary screen's work area on Windows, top-right
/// under the menu bar on Mac. Stays until closed or "open" is clicked. UI thread only.
/// </summary>
public partial class TrayHintWindow : Window
{
    private const string CloseGlyph = "M6 6l12 12M18 6 6 18";
    private const int EdgeMargin = 16;

    private readonly bool _mac;

    public TrayHintWindow() : this(false) { }

    public TrayHintWindow(bool mac)
    {
        _mac = mac;
        InitializeComponent();
        CloseButton.Content = new StrokeIcon { Data = CloseGlyph, Size = 18 };
        ToolTip.SetTip(CloseButton, Loc.Get("ToastClose"));
        Avalonia.Automation.AutomationProperties.SetName(CloseButton, Loc.Get("ToastClose"));
        BodyText.Text = Loc.Get(mac ? "TrayHintBodyMac" : "TrayHintBodyWin");
        CloseButton.Click += (_, _) => Close();
        OpenButton.Click += (_, _) =>
        {
            OpenHomeClicked?.Invoke(this, EventArgs.Empty);
            Close();
        };
        Opened += (_, _) =>
        {
            OverlayPlatform?.ConfigureToast(TryGetPlatformHandle()?.Handle ?? 0);
            Place();
        };
    }

    /// <summary>Keeps the window topmost and non-activating (but clickable) once it has a handle.</summary>
    public IOverlayWindowPlatform? OverlayPlatform { get; init; }

    /// <summary>"เปิดหน้าหลัก" was clicked (the window closes right after).</summary>
    public event EventHandler? OpenHomeClicked;

    private void Place()
    {
        if (Screens is not { } screens || (screens.Primary ?? screens.All.FirstOrDefault()) is not { } screen)
            return;
        var area = screen.WorkingArea; // excludes the taskbar / menu bar
        double scaling = screen.Scaling;
        int margin = (int)Math.Round(EdgeMargin * scaling);
        int width = (int)Math.Ceiling(Width * scaling);
        int height = (int)Math.Ceiling(Bounds.Height * scaling);
        int top = _mac ? area.Y + margin : area.Bottom - margin - height;
        Position = new PixelPoint(area.Right - width - margin, top);
    }
}
