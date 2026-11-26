using System.Diagnostics;
using System.Globalization;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PSRemoteSub.App.Views.Controls;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Net;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;
using QRCoder;

namespace PSRemoteSub.App.Views.Pages;

/// <summary>
/// "ดูบนมือถือ" (mockups New-Settings-Phone and New-Settings-Phone-Problems). Logic ported from the web tab of the old
/// settings window: enable (<see cref="AppHost.SetWebEnabledAsync"/>), addresses and QR, client count, port apply,
/// firewall check and open. New: an ordered problem list (firewall, then the network profile tip with a button that
/// opens <c>ms-settings:network</c>), and the port under "ขั้นสูง" with a "use the platform default" button on a bad port.
/// </summary>
/// <remarks>
/// UI thread only. The app has no way to read the Windows network profile (Public/Private), so step 2 is a standing tip
/// shown on Windows only (where <see cref="AppHost.LanAccess"/> is not null, exactly as the firewall rows). A newer LAN
/// or firewall request drops an older one's result; results that arrive after the page was left are ignored (the page
/// reloads when shown again). The QR bitmap is disposed when the page leaves the screen.
/// </remarks>
public sealed class PhonePage : SettingsPage
{
    private const int MinPort = 1024;
    private const int MaxPort = 65535;
    private const string CircleCheckGlyph = "M12 21.5a9.5 9.5 0 1 0 0-19 9.5 9.5 0 0 0 0 19zM8 12.3l2.7 2.7L16 9.6";
    private const string CircleInfoGlyph = "M12 21.5a9.5 9.5 0 1 0 0-19 9.5 9.5 0 0 0 0 19zM12 11v5.5M12 7.5h.01";
    private const string CheckGlyph = "M5 12.5l4.5 4.5L19 7.5";
    private const string WarnGlyph = "M12 3.5 2.5 20h19L12 3.5zM12 10v4.5M12 17.3h.01";
    private const string RefreshGlyph = "M20 11.5a8 8 0 1 1-2.4-5.7M20 4v5h-5";
    private const string OpenGlyph = "M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5";
    private const string CopyGlyph = "M9 9h11v11H9zM5 15H4V4h11v1";
    private const string ShieldGlyph = "M12 3l8 3v6c0 4.5-3.4 8.3-8 9-4.6-.7-8-4.5-8-9V6zM8.5 12l2.5 2.5 4.5-4.5";
    private const string PhoneGlyph = "M8 2h8a1.5 1.5 0 0 1 1.5 1.5v17A1.5 1.5 0 0 1 16 22H8a1.5 1.5 0 0 1-1.5-1.5v-17A1.5 1.5 0 0 1 8 2zM11 18.5h2";
    private const string ChevronDown = "M6 9l6 6 6-6";
    private const string ChevronUp = "M6 15l6-6 6 6";

    private enum Firewall { Unknown, Checking, Open, Blocked }

    private readonly ToggleSwitch _toggle = new() { Classes = { "tg" } };
    private readonly StackPanel _qrHolder = new();
    private readonly StackPanel _statusHolder = new() { Spacing = 16 };
    private readonly TextBox _portBox = new() { Classes = { "in" }, MaxLength = 5, Width = 120 };
    private readonly Button _portApply = new() { Content = Loc.Get("PortApply") };
    private readonly Control _portErrorRow;
    private readonly TextBlock _portErrorText = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _portDefault = new();
    private readonly StackPanel _advancedBody;
    private readonly StrokeIcon _chevron = new() { Data = ChevronDown, Size = 20, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _advancedSub = new() { Classes = { "sub" } };
    private readonly Image _qrImage = new() { Width = 184, Height = 184 };

    private IReadOnlyList<string> _addresses = [];   // "ip:port"
    private bool _lanKnown;
    private Firewall _firewall = Firewall.Unknown;
    private bool _firewallFailed;
    private bool _firewallBusy;
    private bool _webBusy;
    private int _lanSeq;
    private int _firewallSeq;
    private Bitmap? _qr;

    public PhonePage(AppHost host) : base(host)
    {
        RenderOptions.SetBitmapInterpolationMode(_qrImage, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        _portApply.Classes.Add("btn");
        _portApply.Classes.Add("sm");
        _portDefault.Classes.Add("btn");
        _portDefault.Classes.Add("sm");
        _portDefault.Content = Loc.Format("PhonePortUseDefault", DefaultPort);

        _toggle.IsCheckedChanged += (_, _) =>
        {
            if (!Loading)
                _ = SetWebEnabledAsync();
        };
        _portApply.Click += (_, _) => _ = ApplyPortAsync();
        _portDefault.Click += (_, _) =>
        {
            _portBox.Text = DefaultPort.ToString(CultureInfo.InvariantCulture);
            _ = ApplyPortAsync();
        };
        _portBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            _ = ApplyPortAsync();
        };

        var errorIcon = new StrokeIcon { Data = WarnGlyph, Size = 16, VerticalAlignment = VerticalAlignment.Center };
        Tint(errorIcon, StrokeIcon.ForegroundProperty, "BadBrush");
        Tint(_portErrorText, TextBlock.ForegroundProperty, "BadBrush");
        _portErrorRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            IsVisible = false,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Margin = new Thickness(0, 0, 12, 0),
                    Children = { errorIcon, _portErrorText },
                },
                _portDefault,
            },
        };

        var portRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Classes = { "mut" }, Text = Loc.Get("PortLabel"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) },
                _portBox,
                Wrap(_portApply, new Thickness(12, 0, 0, 0)),
            },
        };
        _advancedBody = new StackPanel { Spacing = 10, IsVisible = false, Children = { portRow, _portErrorRow } };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12, Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent };
        var headerText = new StackPanel { Children = { new TextBlock { Classes = { "lbl" }, Text = Loc.Get("PhoneAdvanced") }, _advancedSub } };
        header.Children.Add(headerText);
        Grid.SetColumn(_chevron, 1);
        header.Children.Add(_chevron);
        header.PointerPressed += (_, _) => SetAdvancedOpen(!_advancedBody.IsVisible);
        var advanced = new Border
        {
            Classes = { "section" },
            Child = new StackPanel { Spacing = 14, Children = { header, _advancedBody } },
        };

        var title = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = Loc.Get("PhoneTitle"), Classes = { "h1" } },
                new TextBlock { Text = Loc.Get("PhoneSub"), Classes = { "mut" }, TextWrapping = TextWrapping.Wrap },
            },
        };
        var enable = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Bottom,
            Children = { new TextBlock { Classes = { "lbl" }, Text = Loc.Get("PhoneEnable"), VerticalAlignment = VerticalAlignment.Center }, _toggle },
        };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16, Margin = new Thickness(0, 0, 0, 2) };
        top.Children.Add(title);
        Grid.SetColumn(enable, 1);
        top.Children.Add(enable);

        SetBody(PageBody(top, _qrHolder, _statusHolder, advanced));
        Loading = true;
        try
        {
            _toggle.IsChecked = Settings.WebEnabled;
        }
        finally
        {
            Loading = false;
        }
        RenderAll();

        DetachedFromVisualTree += (_, _) => DisposeQr();
    }

    /// <summary>The platform's default web port: the Mac value on macOS (port 5000 is AirPlay there), else the settings default.</summary>
    private static int DefaultPort => Program.Platform.Kind == PlatformKind.MacOS
        ? MacSettingsMigration.MacDefaultWebPort
        : new AppSettings().WebPort;

    // ---- lifetime ------------------------------------------------------------------------------------------------

    public override void OnShown()
    {
        LoadWebState();
        _portBox.Text = Settings.WebPort.ToString(CultureInfo.InvariantCulture);
        _portErrorRow.IsVisible = false;
        _portBox.Classes.Remove("bad");
        _ = RefreshLanAsync();
        _ = CheckFirewallAsync();
    }

    /// <summary>A typed, valid, not yet applied port is applied when the page is left (the old window only had the button).</summary>
    public override void CommitPending()
    {
        string text = (_portBox.Text ?? "").Trim();
        if (TryParsePort(text, out int port) && port != Settings.WebPort && !_webBusy)
            _ = ApplyPortAsync();
    }

    protected override void OnHostStateChangedCore() => LoadWebState();

    // ---- web state (ported) --------------------------------------------------------------------------------------

    private void LoadWebState()
    {
        Loading = true;
        try
        {
            _toggle.IsChecked = Settings.WebEnabled;
        }
        finally
        {
            Loading = false;
        }
        RenderAll();
    }

    private async Task SetWebEnabledAsync()
    {
        try
        {
            await Host.SetWebEnabledAsync(_toggle.IsChecked == true);
            Host.SaveSettings();
            LoadWebState();
        }
        catch (Exception)
        {
            // AppHost reports start failures with a notification.
        }
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= MinPort and <= MaxPort;

    private async Task ApplyPortAsync()
    {
        try
        {
            string text = (_portBox.Text ?? "").Trim();
            bool valid = TryParsePort(text, out int port);
            ShowPortError(valid ? null : text);
            if (!valid || _webBusy)
                return;
            _portBox.Text = port.ToString(CultureInfo.InvariantCulture);

            _webBusy = true;
            _portApply.IsEnabled = false;
            _portDefault.IsEnabled = false;
            try
            {
                bool changed = port != Settings.WebPort;
                Settings.WebPort = port;
                // Restarts the server on the new port; on the same port it retries a server that failed to start.
                await Host.SetWebEnabledAsync(Settings.WebEnabled);
                Host.SaveSettings();
                LoadWebState();
                if (changed)
                {
                    _ = RefreshLanAsync();
                    _ = CheckFirewallAsync();
                }
            }
            finally
            {
                _webBusy = false;
                _portApply.IsEnabled = true;
                _portDefault.IsEnabled = true;
            }
        }
        catch (Exception)
        {
            // AppHost reports start failures with a notification.
        }
    }

    /// <summary>Shows the invalid-port message for <paramref name="badText"/>, or hides it when null.</summary>
    private void ShowPortError(string? badText)
    {
        _portErrorRow.IsVisible = badText is not null;
        if (badText is null)
        {
            _portBox.Classes.Remove("bad");
            return;
        }
        _portErrorText.Text = badText.Length > 0 && badText.All(char.IsAsciiDigit) && badText.Length <= 5
            ? Loc.Format("PhonePortOutOfRange", badText)
            : Loc.Get("PortInvalid");
        _portDefault.Content = Loc.Format("PhonePortUseDefault", DefaultPort);
        if (!_portBox.Classes.Contains("bad"))
            _portBox.Classes.Add("bad");
        SetAdvancedOpen(true);
    }

    private void SetAdvancedOpen(bool open)
    {
        _advancedBody.IsVisible = open;
        _chevron.Data = open ? ChevronUp : ChevronDown;
    }

    private async Task RefreshLanAsync()
    {
        try
        {
            int seq = ++_lanSeq;
            int port = Settings.WebPort;
            var candidates = await Task.Run(() =>
            {
                try
                {
                    return LanAddresses.GetCandidates();
                }
                catch (Exception)
                {
                    return (IReadOnlyList<IPAddress>)[];
                }
            });
            if (seq != _lanSeq || !this.IsAttachedToVisualTree())
                return;

            _addresses = candidates.Select(ip => $"{ip}:{port}").ToList();
            _lanKnown = true;
            ShowQr(_addresses.Count > 0 ? $"http://{_addresses[0]}/" : null);
            RenderAll();
        }
        catch (Exception)
        {
            // The list stays as it was.
        }
    }

    private void ShowQr(string? url)
    {
        DisposeQr();
        if (url is null)
            return;

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var code = new PngByteQRCode(data);
        byte[] png = code.GetGraphic(8);
        using var stream = new MemoryStream(png);
        _qr = new Bitmap(stream);
        _qrImage.Source = _qr;
    }

    private void DisposeQr()
    {
        _qrImage.Source = null;
        _qr?.Dispose();
        _qr = null;
    }

    /// <summary><see cref="ILanAccessSetup.IsPortOpen"/> blocks for up to 10 s: thread pool only.</summary>
    private async Task CheckFirewallAsync()
    {
        try
        {
            var lan = Host.LanAccess;
            if (lan is null)
            {
                // This OS has no per-port firewall rule to set up: no firewall, network or cancel-hint rows.
                _firewall = Firewall.Unknown;
                RenderAll();
                return;
            }
            int seq = ++_firewallSeq;
            int port = Settings.WebPort;
            _firewall = Firewall.Checking;
            RenderAll();

            bool open = await Task.Run(() => lan.IsPortOpen(port));
            if (seq != _firewallSeq || !this.IsAttachedToVisualTree())
                return;
            _firewall = open ? Firewall.Open : Firewall.Blocked;
            RenderAll();
        }
        catch (Exception)
        {
            // Leave the rows as they are.
        }
    }

    private async Task OpenFirewallAsync()
    {
        try
        {
            _firewallFailed = false;
            _firewallBusy = true;
            RenderAll();
            bool ok;
            try
            {
                ok = Host.LanAccess is { } lan && await lan.OpenPortAsync(Settings.WebPort);
            }
            catch (Exception ex)
            {
                Log.Warn("Settings: opening the firewall failed", ex);
                ok = false;
            }
            _firewallBusy = false;
            _firewallFailed = !ok;
            await CheckFirewallAsync();
        }
        catch (Exception)
        {
            // Leave the rows as they are.
        }
    }

    private static void OpenNetworkSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings: opening network settings failed: {ex.GetType().Name}");
        }
    }

    private async Task CopyAsync(string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // Nothing to copy to.
        }
    }

    // ---- rendering -----------------------------------------------------------------------------------------------

    private void RenderAll()
    {
        bool windows = Host.LanAccess is not null;
        bool enabled = Settings.WebEnabled;
        bool running = Host.IsWebRunning;
        _advancedSub.Text = Loc.Format("PhoneAdvancedSub", Settings.WebPort);

        RenderQr(enabled && running);

        _statusHolder.Children.Clear();
        if (windows && _firewall == Firewall.Blocked)
            RenderProblems(running);
        else
            RenderChecks(windows, running, enabled);
    }

    private void RenderQr(bool live)
    {
        _qrHolder.Children.Clear();
        if (_lanKnown && _addresses.Count == 0)
        {
            _qrHolder.Children.Add(Note("bad", WarnGlyph, Loc.Get("NoLan")));
            return;
        }
        if (_addresses.Count == 0)
            return;

        var qrFrame = new Border
        {
            Width = 212,
            Height = 212,
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(14),
            Background = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _qrImage,
        };

        var steps = new StackPanel { Spacing = 14 };
        steps.Children.Add(new TextBlock { Classes = { "h2" }, Text = Loc.Get("PhoneScanTitle") });
        string[] stepKeys = ["PhoneScanStep1", "PhoneScanStep2", "PhoneScanStep3"];
        for (int i = 0; i < stepKeys.Length; i++)
        {
            var number = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = (i + 1).ToString(CultureInfo.InvariantCulture), FontWeight = FontWeight.Bold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Tint(number, Border.BackgroundProperty, "Card2Brush");
            Tint(number, Border.BorderBrushProperty, "Border2Brush");
            steps.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children = { number, new TextBlock { Text = Loc.Get(stepKeys[i]), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap } },
            });
        }
        steps.Children.Add(Separator());
        steps.Children.Add(new TextBlock { Classes = { "sub" }, FontWeight = FontWeight.SemiBold, Text = Loc.Get("PhoneTypeAddress"), TextWrapping = TextWrapping.Wrap });
        foreach (string address in _addresses)
            steps.Children.Add(AddressRow(address));
        if (Host.WebClients > 0 || Host.IsWebRunning)
            steps.Children.Add(ClientsPill(Host.WebClients));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 32, Opacity = live ? 1 : 0.45 };
        grid.Children.Add(qrFrame);
        Grid.SetColumn(steps, 1);
        grid.Children.Add(steps);
        _qrHolder.Children.Add(new Border { Classes = { "section" }, Child = grid });
    }

    private Control AddressRow(string address)
    {
        var box = new Border
        {
            Height = 40,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 0),
            Child = new SelectableTextBlock { Text = address, FontSize = 17, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
        };
        Tint(box, Border.BackgroundProperty, "Card2Brush");
        Tint(box, Border.BorderBrushProperty, "Border2Brush");
        var copy = IconButton(Loc.Get("PhoneCopy"), CopyGlyph, "btn", () => _ = CopyAsync(address));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(box);
        Grid.SetColumn(copy, 1);
        row.Children.Add(copy);
        return row;
    }

    private static Control ClientsPill(int count)
    {
        var pill = new Border
        {
            Classes = { "pill", "info" },
            Height = double.NaN,
            MinHeight = 26,
            Padding = new Thickness(10, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new StrokeIcon { Data = PhoneGlyph, Size = 14, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = Loc.Format("PhoneClients", count), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        return pill;
    }

    /// <summary>Not blocked (or not Windows): the connection checks card.</summary>
    private void RenderChecks(bool windows, bool running, bool enabled)
    {
        var rows = new StackPanel();
        var recheck = IconButton(Loc.Get("PhoneRecheck"), RefreshGlyph, "btn sm", () =>
        {
            _ = RefreshLanAsync();
            _ = CheckFirewallAsync();
        });
        recheck.VerticalAlignment = VerticalAlignment.Center;
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        head.Children.Add(SectionHeader(Loc.Get("PhoneChecksTitle"), Loc.Get("PhoneChecksSub")));
        Grid.SetColumn(recheck, 1);
        head.Children.Add(recheck);
        rows.Children.Add(head);

        rows.Children.Add(CheckRow(running ? "ok" : "warn", running ? Loc.Get("PhoneWebRunning") : Loc.Get("PhoneWebStopped"),
            running ? Loc.Format("PhonePortSub", Settings.WebPort) : (enabled ? null : Loc.Get("PhoneWebStoppedSub")), null));
        if (windows)
        {
            rows.Children.Add(Separator());
            rows.Children.Add(_firewall == Firewall.Open
                ? CheckRow("ok", Loc.Get("PhoneFirewallOk"), Loc.Get("PhoneFirewallOkSub"), null)
                : CheckRow("info", Loc.Get("PhoneFirewallChecking"), null, null));
            rows.Children.Add(Separator());
            rows.Children.Add(CheckRow("info", Loc.Get("PhoneNetworkTipTitle"), Loc.Get("NetworkHint"),
                IconButton(Loc.Get("PhoneNetworkButton"), OpenGlyph, "btn sm", OpenNetworkSettings)));
        }
        _statusHolder.Children.Add(new Border { Classes = { "section" }, Child = rows });
        if (windows)
            _statusHolder.Children.Add(new TextBlock { Classes = { "sub" }, Text = Loc.Get("FirewallCancelHint"), TextWrapping = TextWrapping.Wrap });
    }

    /// <summary>Firewall blocked (Windows): the ordered problem list, firewall first, then the network tip.</summary>
    private void RenderProblems(bool running)
    {
        var banner = new Border { Classes = { "note", "bad" }, Padding = new Thickness(20, 16) };
        var bannerText = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = Loc.Get("PhoneProblemsTitle"), FontWeight = FontWeight.SemiBold, FontSize = 17, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = Loc.Get("PhoneProblemsSub"), Classes = { "mut" }, FontSize = 14, TextWrapping = TextWrapping.Wrap },
            },
        };
        banner.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Children = { new StrokeIcon { Data = WarnGlyph, Size = 24, VerticalAlignment = VerticalAlignment.Center }, bannerText },
        };
        _statusHolder.Children.Add(banner);

        var firewallButton = IconButton(Loc.Get("PhoneFirewallButton"), ShieldGlyph, "btn primary", () => _ = OpenFirewallAsync());
        firewallButton.IsEnabled = !_firewallBusy;
        var firewallAction = new StackPanel { Spacing = 8, Children = { firewallButton } };
        if (_firewallFailed)
        {
            var failed = new TextBlock { Text = Loc.Get("FirewallFailed"), FontSize = 14, TextWrapping = TextWrapping.Wrap };
            Tint(failed, TextBlock.ForegroundProperty, "BadBrush");
            firewallAction.Children.Add(failed);
        }
        _statusHolder.Children.Add(ProblemCard(StepMarker("1", "bad"), true, Loc.Get("PhoneFirewallBlocked"), Loc.Get("PhoneFirewallBlockedSub"), firewallAction));
        _statusHolder.Children.Add(ProblemCard(StepMarker("2", "warn"), false, Loc.Get("PhoneNetworkTitle"), Loc.Get("PhoneNetworkSub"),
            IconButton(Loc.Get("PhoneNetworkButton"), OpenGlyph, "btn", OpenNetworkSettings)));
        _statusHolder.Children.Add(ProblemCard(StepMarker(null, running ? "ok" : "warn"), false,
            running ? Loc.Get("PhoneWebRunning") : Loc.Get("PhoneWebStopped"),
            running ? Loc.Format("PhonePortSub", Settings.WebPort) : null, null));
        _statusHolder.Children.Add(new TextBlock { Classes = { "sub" }, Text = Loc.Get("FirewallCancelHint"), TextWrapping = TextWrapping.Wrap });
    }

    // ---- small builders ------------------------------------------------------------------------------------------

    private static Border StepMarker(string? number, string kind)
    {
        string fg = kind switch { "bad" => "BadBrush", "warn" => "WarnBrush", _ => "OkBrush" };
        string bg = kind switch { "bad" => "BadBgBrush", "warn" => "WarnBgBrush", _ => "OkBgBrush" };
        Control content;
        if (number is null)
        {
            var icon = new StrokeIcon { Data = CheckGlyph, Size = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Tint(icon, StrokeIcon.ForegroundProperty, fg);
            content = icon;
        }
        else
        {
            var text = new TextBlock { Text = number, FontWeight = FontWeight.Bold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Tint(text, TextBlock.ForegroundProperty, fg);
            content = text;
        }
        var marker = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Top, Child = content };
        Tint(marker, Border.BackgroundProperty, bg);
        return marker;
    }

    private static Border ProblemCard(Control marker, bool bad, string title, string? sub, Control? action)
    {
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 17, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(sub))
            body.Children.Add(new TextBlock { Text = sub, Classes = { "mut" }, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (action is not null)
        {
            action.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(action);
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16 };
        grid.Children.Add(marker);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        var card = new Border
        {
            Padding = new Thickness(22, 18),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
        Tint(card, Border.BackgroundProperty, "CardBrush");
        Tint(card, Border.BorderBrushProperty, bad ? "BadBrush" : "BorderBrush");
        return card;
    }

    /// <summary>A status row: icon (ok = check circle, warn = warning triangle, info = info circle), title, sub, optional button.</summary>
    private static Control CheckRow(string kind, string title, string? sub, Control? action)
    {
        string glyph = kind switch { "ok" => CircleCheckGlyph, "warn" => WarnGlyph, _ => CircleInfoGlyph };
        string brush = kind switch { "ok" => "OkBrush", "warn" => "WarnBrush", _ => "InfoBrush" };
        var icon = new StrokeIcon { Data = glyph, Size = 22, VerticalAlignment = VerticalAlignment.Center };
        Tint(icon, StrokeIcon.ForegroundProperty, brush);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Classes = { "lbl" }, Text = title, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(sub))
            text.Children.Add(new TextBlock { Classes = { "sub" }, FontSize = 14, Text = sub, TextWrapping = TextWrapping.Wrap });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, Margin = new Thickness(0, 10) };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (action is not null)
        {
            action.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(action, 2);
            grid.Children.Add(action);
        }
        return grid;
    }

    private static Border Note(string kind, string glyph, string text) => new()
    {
        Classes = { "note", kind },
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new StrokeIcon { Data = glyph, Size = 18, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
            },
        },
    };

    private static Border Separator()
    {
        var line = new Border { Height = 1 };
        line.Classes.Add("hr");
        return line;
    }

    private static Control Wrap(Control child, Thickness margin)
    {
        child.Margin = margin;
        return child;
    }

    private static void Tint(AvaloniaObject target, AvaloniaProperty property, string brushKey)
    {
        if (target is Control control)
            control.Bind(property, control.GetResourceObservable(brushKey));
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
