using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using PSRemoteSub.App.Views.Controls;
using PSRemoteSub.Core.Input;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App.Views.Pages;

/// <summary>
/// "ปุ่มลัดและอื่น ๆ" (mockup New-Settings-General). Ported from the general tab of the old settings window: keep-awake,
/// region frames (<see cref="AppHost.SetRegionFramesEnabled"/>), the version, and the hotkeys. New: hotkeys are changed by
/// recording the next chord (a conflict offers "try another" or "swap", <see cref="HotkeyAssign"/>), and the app theme.
/// </summary>
/// <remarks>
/// UI thread only. The old text boxes and <c>CommitHotkeys</c> are gone: a recorded chord always parses (modifier plus
/// letter or digit, the <see cref="HotkeySpec"/> rules) and a duplicate is resolved in the conflict step, so the stored set
/// stays valid and unique. While recording, the global hotkeys are unregistered through
/// <see cref="AppHost.SetHotkeysSuspended"/> so the pressed chord cannot fire its action; they are registered again when
/// recording ends (applied, cancelled with Esc or the cancel button, page left, window deactivated). The hotkey list is
/// rebuilt with fresh controls on every state change.
/// </remarks>
public sealed class GeneralPage : SettingsPage
{
    private static readonly HotkeyAction[] Order =
    [
        HotkeyAction.TogglePause, HotkeyAction.SelectRegion, HotkeyAction.ToggleOverlay, HotkeyAction.SaveFrame,
        HotkeyAction.ToggleRegionFrames,
    ];

    private readonly StackPanel _hotkeyList = new();
    private readonly RadioButton _themeDark = new() { Classes = { "seg" }, GroupName = "GeneralTheme", Margin = new Thickness(0, 0, 4, 0) };
    private readonly RadioButton _themeLight = new() { Classes = { "seg" }, GroupName = "GeneralTheme", Margin = new Thickness(0, 0, 4, 0) };
    private readonly RadioButton _themeSystem = new() { Classes = { "seg" }, GroupName = "GeneralTheme" };
    private readonly ToggleSwitch _keepAwake = new() { Classes = { "tg" } };
    private readonly ToggleSwitch _regionFrames = new() { Classes = { "tg" } };

    private HotkeyAction? _recording;
    private (HotkeyAction Other, string Key)? _conflict;
    private bool _recordingNeedsModifier;
    private Window? _window;

    public GeneralPage(AppHost host) : base(host)
    {
        _themeDark.Content = ThemeLabel("M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z", Loc.Get("GeneralThemeDark"));
        _themeLight.Content = ThemeLabel(
            "M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM12 2.5v2M12 19.5v2M4.6 4.6l1.4 1.4M18 18l1.4 1.4M2.5 12h2M19.5 12h2M4.6 19.4 6 18M18 6l1.4-1.4",
            Loc.Get("GeneralThemeLight"));
        _themeSystem.Content = Loc.Get(host.Platform.Kind == PlatformKind.MacOS ? "GeneralThemeSystemMac" : "GeneralThemeSystem");
        WireTheme(_themeDark, AppTheme.Dark);
        WireTheme(_themeLight, AppTheme.Light);
        WireTheme(_themeSystem, AppTheme.System);

        _keepAwake.IsCheckedChanged += (_, _) => Safe(() =>
        {
            if (Loading)
                return;
            Settings.KeepAwake = _keepAwake.IsChecked == true;
            Host.ApplySettings();
            ScheduleSave();
        });
        _regionFrames.IsCheckedChanged += (_, _) => Safe(() =>
        {
            if (Loading || (_regionFrames.IsChecked == true) == Settings.ShowRegionFrames)
                return;
            Host.SetRegionFramesEnabled(_regionFrames.IsChecked == true); // applies and saves
        });

        var theme = FormRow(Loc.Get("GeneralThemeLabel"),
            Loc.Format("GeneralThemeSub", Loc.Get(host.Platform.Kind == PlatformKind.MacOS ? "GeneralThemeSystemMac" : "GeneralThemeSystem")),
            new Border
            {
                Classes = { "seg" },
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _themeDark, _themeLight, _themeSystem } },
            }, controlWidth: 330);

        var other = new StackPanel
        {
            Spacing = 18,
            Children =
            {
                theme,
                new Border { Classes = { "hr" } },
                FormToggleRow(Loc.Get("GeneralKeepAwake"), Loc.Get("GeneralKeepAwakeSub"), _keepAwake),
                new Border { Classes = { "hr" } },
                FormToggleRow(Loc.Get("GeneralRegionFrames"), Loc.Get("GeneralRegionFramesSub"), _regionFrames),
            },
        };

        var hotkeys = new Border
        {
            Classes = { "section" },
            Child = new StackPanel
            {
                Spacing = 8,
                Children = { SectionHeader(Loc.Get("HotkeysHeader"), Loc.Get("GeneralHotkeysSub")), _hotkeyList },
            },
        };

        SetBody(new StackPanel
        {
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 16,
            Children =
            {
                PageHeader(Loc.Get("GeneralTitle"), Loc.Get("GeneralSub")),
                hotkeys,
                Section(Loc.Get("GeneralOtherHeader"), null, other),
                new TextBlock { Text = Loc.Format("VersionLabel", AppHost.Version), Classes = { "sub" }, Margin = new Thickness(4, 0) },
            },
        });

        AddHandler(KeyDownEvent, OnRecordKeyDown, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) =>
        {
            if (Application.Current is { } app)
                app.ActualThemeVariantChanged += OnThemeVariantChanged;
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window is not null)
                _window.Deactivated += OnWindowDeactivated;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (Application.Current is { } app)
                app.ActualThemeVariantChanged -= OnThemeVariantChanged;
            if (_window is not null)
                _window.Deactivated -= OnWindowDeactivated;
            _window = null;
            Safe(CancelRecording);
        };
        LoadAll();
    }

    public override void OnShown()
    {
        CancelRecording();
        LoadAll();
    }

    public override void CommitPending() => CancelRecording(); // only resumes the hotkeys; nothing else is typed here

    protected override void OnHostStateChangedCore()
    {
        // A tray toggle may have changed the region frames; the list itself is not rebuilt while recording.
        if (_regionFrames.IsChecked != Settings.ShowRegionFrames)
            LoadToggles();
    }

    // ---- loading --------------------------------------------------------------------------------------------------

    private void LoadAll()
    {
        LoadToggles();
        LoadTheme();
        RenderHotkeys();
    }

    private void LoadToggles()
    {
        bool was = Loading;
        Loading = true;
        try
        {
            _keepAwake.IsChecked = Settings.KeepAwake;
            _regionFrames.IsChecked = Settings.ShowRegionFrames;
        }
        finally
        {
            Loading = was;
        }
    }

    // ---- theme ----------------------------------------------------------------------------------------------------

    private static Control ThemeLabel(string glyph, string text) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Children =
        {
            new StrokeIcon { Data = glyph, Size = 16 },
            new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
        },
    };

    private void WireTheme(RadioButton button, AppTheme theme) =>
        button.IsCheckedChanged += (_, _) => Safe(() =>
        {
            if (Loading || button.IsChecked != true || Settings.Theme == theme)
                return;
            Settings.Theme = theme;
            if (Application.Current is { } app)
                ThemeApplier.Apply(app, theme); // MainWindow's sun/moon button follows ActualThemeVariantChanged
            ScheduleSave();
        });

    private void LoadTheme()
    {
        bool was = Loading;
        Loading = true;
        try
        {
            _themeDark.IsChecked = Settings.Theme == AppTheme.Dark;
            _themeLight.IsChecked = Settings.Theme == AppTheme.Light;
            _themeSystem.IsChecked = Settings.Theme == AppTheme.System;
        }
        finally
        {
            Loading = was;
        }
    }

    /// <summary>The header toggle changed the theme: show it here too.</summary>
    private void OnThemeVariantChanged(object? sender, EventArgs e) => Safe(LoadTheme);

    // ---- hotkeys --------------------------------------------------------------------------------------------------

    private static string ActionName(HotkeyAction action) => Loc.Get("General" + action);

    private static string ActionSub(HotkeyAction action) => Loc.Get("General" + action + "Sub");

    private Control Caps(string? setting)
    {
        var caps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var cap in HotkeyLabels.KeyCaps(setting, Host.Platform.Kind))
            caps.Children.Add(new Border { Classes = { "kbd" }, Child = new TextBlock { Text = cap } });
        return caps;
    }

    private void RenderHotkeys()
    {
        _hotkeyList.Children.Clear();
        for (int i = 0; i < Order.Length; i++)
        {
            var action = Order[i];
            _hotkeyList.Children.Add(_recording == action ? RecordingRow(action) : HotkeyRow(action));
            if (i < Order.Length - 1 && _recording != action && _recording != Order[i + 1])
                _hotkeyList.Children.Add(new Border { Classes = { "hr" } });
        }
    }

    private Control HotkeyRow(HotkeyAction action)
    {
        var change = new Button { Classes = { "btn", "sm" }, Content = Loc.Get("GeneralChange"), VerticalAlignment = VerticalAlignment.Center };
        change.Click += (_, _) => Safe(() => StartRecording(action));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 16, Margin = new Thickness(0, 12) };
        grid.Children.Add(FormLabel(ActionName(action), ActionSub(action)));
        var caps = Caps(Settings.Hotkeys.GetValueOrDefault(action));
        Grid.SetColumn(caps, 1);
        grid.Children.Add(caps);
        Grid.SetColumn(change, 2);
        grid.Children.Add(change);
        return grid;
    }

    private Control RecordingRow(HotkeyAction action)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 12 };
        header.Children.Add(new TextBlock { Classes = { "lbl" }, Text = ActionName(action), VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var caps = Caps(Settings.Hotkeys.GetValueOrDefault(action));
        Grid.SetColumn(caps, 1);
        header.Children.Add(caps);
        var waiting = new TextBlock { Classes = { "sub" }, Text = Loc.Get("GeneralRecording"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(waiting, 2);
        header.Children.Add(waiting);

        var stack = new StackPanel { Spacing = 10, Children = { header } };

        if (_conflict is { } conflict)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 10 };
            line.Children.Add(new StrokeIcon { Data = "M12 3.5 2.5 20h19L12 3.5zM12 10v4.5M12 17.3h.01", Size = 16, VerticalAlignment = VerticalAlignment.Center });
            var text = new TextBlock
            {
                Text = Loc.Format("GeneralConflict", ActionName(conflict.Other)),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14,
            };
            Grid.SetColumn(text, 1);
            line.Children.Add(text);
            var retry = new Button { Classes = { "btn", "sm" }, Content = Loc.Get("GeneralTryOther") };
            retry.Click += (_, _) => Safe(TryAnother);
            Grid.SetColumn(retry, 2);
            line.Children.Add(retry);
            var swap = new Button { Classes = { "btn", "ghost", "sm" }, Content = Loc.Get("GeneralSwap") };
            swap.Click += (_, _) => Safe(SwapConflict);
            Grid.SetColumn(swap, 3);
            line.Children.Add(swap);
            stack.Children.Add(new Border { Classes = { "note", "warn" }, Child = line });
        }
        else
        {
            var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
            foot.Children.Add(new TextBlock
            {
                Classes = { "sub" },
                Text = Loc.Get(_recordingNeedsModifier ? "GeneralNeedModifier" : "GeneralRecordingHint"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var cancel = new Button { Classes = { "btn", "ghost", "sm" }, Content = Loc.Get("GeneralCancel") };
            cancel.Click += (_, _) => Safe(CancelRecording);
            Grid.SetColumn(cancel, 1);
            foot.Children.Add(cancel);
            stack.Children.Add(foot);
        }

        // Focusable so the chord goes to this page's tunnel handler (and not to a button or box).
        var panel = new Border
        {
            Classes = { "recording" },
            Focusable = true,
            Padding = new Thickness(16, 14),
            Margin = new Thickness(0, 8),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
        panel.Bind(Border.BackgroundProperty, panel.GetResourceObservable("Card2Brush"));
        panel.Bind(Border.BorderBrushProperty, panel.GetResourceObservable("AccentBrush"));
        if (_conflict is null)
            Dispatcher.UIThread.Post(() => panel.Focus(), DispatcherPriority.Input);
        return panel;
    }

    private void StartRecording(HotkeyAction action)
    {
        if (_recording is null)
            Host.SetHotkeysSuspended(true);
        _recording = action;
        _conflict = null;
        _recordingNeedsModifier = false;
        RenderHotkeys();
    }

    /// <summary>Ends recording (if any), registers the global hotkeys again and redraws the list.</summary>
    private void CancelRecording()
    {
        if (_recording is null)
            return;
        _recording = null;
        _conflict = null;
        _recordingNeedsModifier = false;
        Host.SetHotkeysSuspended(false);
        RenderHotkeys();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => Safe(CancelRecording);

    private void TryAnother()
    {
        _conflict = null;
        _recordingNeedsModifier = false;
        RenderHotkeys();
    }

    private void OnRecordKeyDown(object? sender, KeyEventArgs e)
    {
        if (_recording is not { } action)
            return;
        Safe(() =>
        {
            e.Handled = true;
            if (e.Key == Key.Escape)
            {
                CancelRecording();
                return;
            }
            if (_conflict is not null)
                return; // waiting for "try another" / "swap"
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.System)
                return; // a modifier on its own: keep waiting
            if (ChordKey(e.Key, e.PhysicalKey, Host.Platform.Kind == PlatformKind.MacOS) is not { } key)
                return; // not a letter or digit
            var mods = e.KeyModifiers;
            bool ctrl = mods.HasFlag(KeyModifiers.Control), alt = mods.HasFlag(KeyModifiers.Alt), shift = mods.HasFlag(KeyModifiers.Shift);
            if (!(ctrl || alt || shift))
            {
                _recordingNeedsModifier = true;
                RenderHotkeys();
                return;
            }
            Assign(action, new HotkeySpec(ctrl, alt, shift, key).ToString());
        });
    }

    /// <summary>
    /// The letter or digit of a chord. macOS: from the physical key (the global hotkeys are registered by physical ANSI
    /// keycode, and Avalonia's <see cref="Key"/> follows the input source: Thai, Option-altered characters), falling back
    /// to <paramref name="key"/>. Windows: <paramref name="key"/> (VK based, like RegisterHotKey). Null: not a letter or digit.
    /// Kept in App (not Core, where there is no test project access to Avalonia's types).
    /// </summary>
    internal static char? ChordKey(Key key, PhysicalKey physical, bool mac)
    {
        if (mac && PhysicalChar(physical) is { } c)
            return c;
        return ChordKey(key);
    }

    private static char? PhysicalChar(PhysicalKey key)
    {
        if (key is >= PhysicalKey.A and <= PhysicalKey.Z)
            return (char)('A' + (key - PhysicalKey.A));
        if (key is >= PhysicalKey.Digit1 and <= PhysicalKey.Digit9)
            return (char)('1' + (key - PhysicalKey.Digit1));
        if (key == PhysicalKey.Digit0)
            return '0';
        if (key is >= PhysicalKey.NumPad1 and <= PhysicalKey.NumPad9)
            return (char)('1' + (key - PhysicalKey.NumPad1));
        if (key == PhysicalKey.NumPad0)
            return '0';
        return null;
    }

    private static char? ChordKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
            return (char)('A' + (key - Key.A));
        if (key is >= Key.D0 and <= Key.D9)
            return (char)('0' + (key - Key.D0));
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return (char)('0' + (key - Key.NumPad0));
        return null;
    }

    private void Assign(HotkeyAction action, string key)
    {
        var current = new Dictionary<HotkeyAction, string>(Settings.Hotkeys);
        if (HotkeyAssign.ConflictWith(current, action, key) is { } other)
        {
            _conflict = (other, key);
            RenderHotkeys();
            return;
        }
        Settings.Hotkeys[action] = key;
        FinishAssign();
    }

    private void SwapConflict()
    {
        if (_recording is not { } action || _conflict is not { } conflict)
            return;
        // The recorded chord equals the other action's key, so exchanging the two keys gives this action the chord
        // and the other action this action's previous key.
        HotkeyAssign.Swap(Settings.Hotkeys, action, conflict.Other);
        FinishAssign();
    }

    /// <summary>Applies the new set: resume and re-register the hotkeys, push to the app, save now.</summary>
    private void FinishAssign()
    {
        _recording = null;
        _conflict = null;
        _recordingNeedsModifier = false;
        Host.SetHotkeysSuspended(false); // registers the old set; ApplySettings re-registers when it differs
        Host.ApplySettings();
        Host.SaveSettings();
        RenderHotkeys();
    }
}
