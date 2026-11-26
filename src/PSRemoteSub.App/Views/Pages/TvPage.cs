using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PSRemoteSub.App.Views.Controls;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;
using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.App.Views.Pages;

/// <summary>
/// "ดูบนทีวี" (mockups New-Settings-Tv and New-Settings-Tv-Install). Logic ported from the TV tab of the old settings
/// window (<c>SettingsWindow.Tv.cs</c>): the on/off switch, connected state, cover/strip mode, text size, box opacity,
/// strip distance (pushed to the TV live, saved debounced), calibration (under "ขั้นสูง"), installing the TV app over
/// ADB with its progress and errors, diagnostics, and the macOS Local Network hint. New: a 4-step install stepper driven
/// by <see cref="TvInstallStep"/>, shown while nothing is installed or after "เปลี่ยนทีวี"; the connected TV is shown by
/// its ADB address.
/// </summary>
/// <remarks>
/// UI thread only. The whole page is one tree whose parts are shown or hidden (no control changes parent). The
/// calibration pattern is turned off when the page leaves the screen or the window hides, as the old window did on
/// close. An install or diagnostics run keeps going when the page is left; its progress still lands here.
/// </remarks>
public sealed class TvPage : SettingsPage
{
    private const string DefaultAdbPort = "5555";

    private const string TvGlyph = "M3 7h18v11H3zM8 21h8M8.5 3 12 6.5 15.5 3";
    private const string SwapGlyph = "M20 11.5a8 8 0 1 1-2.4-5.7M20 4v5h-5";
    private const string CalibGlyph = "M4 9V4h5M15 4h5v5M20 15v5h-5M9 20H4v-5";
    private const string DownloadGlyph = "M12 4v11M7 10l5 5 5-5M5 20h14";
    private const string ChevronRight = "M9 6l6 6-6 6";
    private const string ChevronLeft = "M15 6l-6 6 6 6";
    private const string ChevronDown = "M6 9l6 6 6-6";
    private const string ChevronUp = "M6 15l6-6 6 6";
    private const string CheckGlyph = "M5 12.5l4.5 4.5L19 7.5";
    private const string WarnGlyph = "M12 3.5 2.5 20h19L12 3.5zM12 10v4.5M12 17.3h.01";
    private const string OpenGlyph = "M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5";

    private enum StepState { Pending, Active, Done }

    // ---- shared
    private readonly ToggleSwitch _enabled = new() { Classes = { "tg" } };
    private readonly Border _localNetNote;
    private readonly Button _diag;
    private readonly TextBlock _diagStep = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(4, 0) };
    private readonly Border _diagError;
    private readonly TextBlock _diagErrorText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _busy = new() { Classes = { "sub" }, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(4, 0), Text = Loc.Get("TvInstallBusy") };

    // ---- connected view
    private readonly Control _mainHeader;
    private readonly Border _stateCard;
    private readonly Border _stateIconBox = new() { Width = 48, Height = 48, CornerRadius = new CornerRadius(12) };
    private readonly StrokeIcon _stateIcon = new() { Data = TvGlyph, Size = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _stateTitle = new() { Classes = { "lbl" }, FontSize = 17, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _stateSub = new() { Classes = { "sub" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _changeTvText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _modeCard;
    private readonly RadioButton _modeCover = new() { Classes = { "opt" }, GroupName = "TvPageMode" };
    private readonly RadioButton _modeStrip = new() { Classes = { "opt" }, GroupName = "TvPageMode" };
    private readonly RadioParts _coverParts;
    private readonly RadioParts _stripParts;
    private readonly Border _sizeCard;
    private readonly Slider _textScale = new() { Minimum = 50, Maximum = 200, SmallChange = 5, LargeChange = 10, TickFrequency = 5, IsSnapToTickEnabled = true };
    private readonly TextBlock _textScaleText = new();
    private readonly Slider _boxOpacity = new() { Minimum = 0, Maximum = 1, SmallChange = 0.05, LargeChange = 0.1, TickFrequency = 0.05, IsSnapToTickEnabled = true };
    private readonly TextBlock _boxOpacityText = new();
    private readonly Slider _stripBottom = new() { Minimum = 0, Maximum = 60, SmallChange = 1, LargeChange = 5, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _stripBottomText = new();
    private readonly Control _stripBottomRow;
    private readonly Border _calibCard;
    private readonly Border _advanced;
    private readonly StackPanel _advancedBody;
    private readonly StrokeIcon _advancedChevron = new() { Data = ChevronDown, Size = 20, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _calibrating = new() { Classes = { "tg" } };
    private readonly Slider _calibScale = new() { Minimum = 0.8, Maximum = 1.2, SmallChange = 0.005, LargeChange = 0.02, TickFrequency = 0.005, IsSnapToTickEnabled = true };
    private readonly TextBlock _calibScaleText = new();
    private readonly Slider _calibX = new() { Minimum = -0.1, Maximum = 0.1, SmallChange = 0.0025, LargeChange = 0.01, TickFrequency = 0.0025, IsSnapToTickEnabled = true };
    private readonly TextBlock _calibXText = new();
    private readonly Slider _calibY = new() { Minimum = -0.1, Maximum = 0.1, SmallChange = 0.0025, LargeChange = 0.01, TickFrequency = 0.0025, IsSnapToTickEnabled = true };
    private readonly TextBlock _calibYText = new();

    // ---- install view
    private readonly Control _installHeader;
    private readonly Border _stepper;
    private readonly StepRow[] _steps = new StepRow[4];
    private readonly TextBox _address = new() { Classes = { "in" }, MaxLength = 64, Width = 220, PlaceholderText = Loc.Get("TvAddressWatermark") };
    private readonly Button _install;
    private readonly Border _installError;
    private readonly TextBlock _installErrorText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock[] _stepStatus = new TextBlock[4];
    private readonly Border _doneNote;
    private readonly Button _finish;

    private int _activeStep = 1;
    private bool _installDone;
    private bool _adbRunHere; // this page started the running ADB work (install or diagnostics), so its own step texts show
    private Window? _window;

    public TvPage(AppHost host) : base(host)
    {
        // ---- header (connected view)
        var title = PageHeader(Loc.Get("NavTv"), Loc.Get("TvPageSub"));
        title.Margin = new Thickness(0);
        var enable = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Bottom,
            Children = { new TextBlock { Classes = { "lbl" }, Text = Loc.Get("TvEnable"), VerticalAlignment = VerticalAlignment.Center }, _enabled },
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16, Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(title);
        Grid.SetColumn(enable, 1);
        header.Children.Add(enable);
        _mainHeader = header;

        // ---- state card
        Tint(_stateIcon, StrokeIcon.ForegroundProperty, "OkBrush");
        Tint(_stateIconBox, Border.BackgroundProperty, "OkBgBrush");
        _stateIconBox.Child = _stateIcon;
        _stateIconBox.VerticalAlignment = VerticalAlignment.Center;
        var changeTv = new Button
        {
            Classes = { "btn", "ghost", "sm" },
            VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { new StrokeIcon { Data = SwapGlyph, Size = 16, VerticalAlignment = VerticalAlignment.Center }, _changeTvText },
            },
        };
        changeTv.Click += (_, _) => Safe(OpenInstaller);
        var stateText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _stateTitle, _stateSub } };
        var stateGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 16 };
        stateGrid.Children.Add(_stateIconBox);
        Grid.SetColumn(stateText, 1);
        stateGrid.Children.Add(stateText);
        Grid.SetColumn(changeTv, 2);
        stateGrid.Children.Add(changeTv);
        _stateCard = new Border { Classes = { "section" }, Padding = new Thickness(22, 18), Child = stateGrid };

        // ---- mode cards
        _coverParts = ModeCard(_modeCover, cover: true, Loc.Get("TvModeCoverTitle"), Loc.Get("TvModeCoverSub"));
        _stripParts = ModeCard(_modeStrip, cover: false, Loc.Get("TvModeStripTitle"), Loc.Get("TvModeStripSub"));
        var modes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 14 };
        modes.Children.Add(_modeCover);
        Grid.SetColumn(_modeStrip, 1);
        modes.Children.Add(_modeStrip);
        _modeCard = Section(Loc.Get("TvModeTitle"), null, modes);

        // ---- size card
        _stripBottomRow = SliderRow(Loc.Get("TvStripBottomLabel"), Loc.Get("TvStripBottomSub"), _stripBottom, _stripBottomText);
        _sizeCard = new Border
        {
            Classes = { "section" },
            Child = new StackPanel
            {
                Spacing = 18,
                Children =
                {
                    new TextBlock { Classes = { "h2" }, Text = Loc.Get("TvSizeTitle") },
                    SliderRow(Loc.Get("TvTextScaleLabel"), null, _textScale, _textScaleText),
                    SliderRow(Loc.Get("TvBoxOpacityLabel"), null, _boxOpacity, _boxOpacityText),
                    _stripBottomRow,
                },
            },
        };

        // ---- calibration card + advanced
        var calibIconBox = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(12),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StrokeIcon { Data = CalibGlyph, Size = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        Tint(calibIconBox, Border.BackgroundProperty, "InfoBgBrush");
        Tint(calibIconBox.Child!, StrokeIcon.ForegroundProperty, "InfoBrush");
        var calibText = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Classes = { "lbl" }, Text = Loc.Get("TvCalibQuestion"), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Classes = { "sub" }, FontSize = 14, Text = Loc.Get("TvCalibQuestionSub"), TextWrapping = TextWrapping.Wrap },
            },
        };
        var calibStart = new Button { Classes = { "btn" }, Content = Loc.Get("TvCalibStart"), VerticalAlignment = VerticalAlignment.Center };
        calibStart.Click += (_, _) => Safe(StartCalibrating);
        var calibGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 16 };
        calibGrid.Children.Add(calibIconBox);
        Grid.SetColumn(calibText, 1);
        calibGrid.Children.Add(calibText);
        Grid.SetColumn(calibStart, 2);
        calibGrid.Children.Add(calibStart);
        _calibCard = new Border { Classes = { "section" }, Padding = new Thickness(22, 18), Child = calibGrid };

        var calibReset = new Button { Classes = { "btn", "sm" }, Content = Loc.Get("TvCalibReset"), HorizontalAlignment = HorizontalAlignment.Left };
        calibReset.Click += (_, _) => Safe(ResetTvCalibration);
        _advancedBody = new StackPanel
        {
            Spacing = 16,
            IsVisible = false,
            Children =
            {
                FormToggleRow(Loc.Get("TvCalibrate"), Loc.Get("TvCalibHintPage"), _calibrating),
                SliderRow(Loc.Get("TvCalibScaleLabel"), null, _calibScale, _calibScaleText),
                SliderRow(Loc.Get("TvCalibXLabel"), null, _calibX, _calibXText),
                SliderRow(Loc.Get("TvCalibYLabel"), null, _calibY, _calibYText),
                calibReset,
            },
        };
        var advHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        advHead.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Classes = { "lbl" }, Text = Loc.Get("PhoneAdvanced") },
                new TextBlock { Classes = { "sub" }, Text = Loc.Get("TvCalibHeader"), TextWrapping = TextWrapping.Wrap },
            },
        });
        Grid.SetColumn(_advancedChevron, 1);
        advHead.Children.Add(_advancedChevron);
        var advButton = new Button
        {
            Content = advHead,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        advButton.Click += (_, _) => Safe(() => SetAdvancedOpen(!_advancedBody.IsVisible));
        _advanced = new Border { Classes = { "section" }, Child = new StackPanel { Spacing = 16, Children = { advButton, _advancedBody } } };

        // ---- install view
        var back = IconButton(Loc.Get("NavTv"), ChevronLeft, "btn ghost sm", () => ShowView(install: false));
        back.Padding = new Thickness(6, 0);
        var installTitle = PageHeader(Loc.Get("TvInstallHeader"), Loc.Get("TvInstallSub"));
        installTitle.Margin = new Thickness(0, 0, 0, 4);
        _installHeader = new StackPanel { Spacing = 10, Children = { back, installTitle } };

        _install = IconButton(Loc.Get("TvInstallButton"), null, "btn primary", () => _ = InstallTvAppAsync());
        _install.Focusable = false; // clicking it must not commit a half-typed field elsewhere
        _installError = ErrorNote(_installErrorText);
        for (int i = 0; i < _stepStatus.Length; i++)
            _stepStatus[i] = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        _finish = IconButton(Loc.Get("TvInstallFinish"), null, "btn primary", () => ShowView(install: false));
        _doneNote = new Border
        {
            Classes = { "note", "ok" },
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    DoneIcon(),
                    new TextBlock { Text = Loc.Get("TvInstallDone"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        Tint(_doneNote, Border.BackgroundProperty, "OkBgBrush");

        var next = IconButton(Loc.Get("TvStep1Next"), CheckGlyph, "btn primary", () => SetStep(2));
        _steps[0] = new StepRow(1, Loc.Get("TvStep1Title"), Loc.Get("TvStep1Sub"),
            new StackPanel { Spacing = 16, Children = { Step1Body(), next } });
        var addressRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Classes = { "lbl" }, Text = Loc.Get("TvAddressLabel"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) },
                WithMargin(_address, new Thickness(0, 0, 12, 0)),
                _install,
            },
        };
        _steps[1] = new StepRow(2, Loc.Get("TvStep2Title"), Loc.Get("TvStep2Sub"), new StackPanel
        {
            Spacing = 10,
            Children =
            {
                addressRow,
                new TextBlock { Classes = { "sub" }, Text = Loc.Get("TvAddressHint"), TextWrapping = TextWrapping.Wrap },
                _stepStatus[1],
                _installError,
            },
        });
        _steps[2] = new StepRow(3, Loc.Get("TvStep3Title"), Loc.Get("TvStep3Sub"), new StackPanel { Spacing = 10, Children = { _stepStatus[2] } });
        _steps[3] = new StepRow(4, Loc.Get("TvStep4Title"), Loc.Get("TvStep4Sub"), new StackPanel { Spacing = 12, Children = { _stepStatus[3], _doneNote, _finish } });
        var stepperStack = new StackPanel();
        for (int i = 0; i < _steps.Length; i++)
        {
            if (i > 0)
                stepperStack.Children.Add(new Border { Classes = { "hr" }, Height = 1 });
            stepperStack.Children.Add(_steps[i].Root);
        }
        _stepper = new Border { Classes = { "card" }, ClipToBounds = true, Child = stepperStack };

        // ---- shared: Local Network hint (macOS), diagnostics, busy line
        var openPerm = IconButton(Loc.Get("PermOpenSettings"), OpenGlyph, "btn sm", () => Host.OpenPermissionSettings(PlatformPermission.LocalNetwork));
        var localNetGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        var localNetIcon = new StrokeIcon { Data = WarnGlyph, Size = 18, VerticalAlignment = VerticalAlignment.Center };
        localNetGrid.Children.Add(localNetIcon);
        var localNetText = new TextBlock { Text = Loc.Get("PermLocalNetwork"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(localNetText, 1);
        localNetGrid.Children.Add(localNetText);
        openPerm.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(openPerm, 2);
        localNetGrid.Children.Add(openPerm);
        _localNetNote = new Border { Classes = { "note", "warn" }, IsVisible = false, Child = localNetGrid };

        _diag = IconButton(Loc.Get("TvDiagButton"), DownloadGlyph, "btn ghost sm", () => _ = CollectTvDiagnosticsAsync());
        _diag.Focusable = false;
        _diag.Padding = new Thickness(6, 0);
        var diagRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 0),
            Children =
            {
                new TextBlock { Classes = { "sub" }, Text = Loc.Get("TvDiagLead"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                WithMargin(_diag, new Thickness(0, 0, 8, 0)),
                new TextBlock { Classes = { "sub" }, Text = Loc.Get("TvDiagTail"), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        _diagError = ErrorNote(_diagErrorText);

        var body = PageBody(_mainHeader, _installHeader, _localNetNote, _stateCard, _modeCard, _sizeCard, _calibCard, _advanced, _stepper,
            diagRow, _diagStep, _diagError, _busy);
        body.MaxWidth = 860;
        body.HorizontalAlignment = HorizontalAlignment.Left;
        SetBody(body);

        WireEvents();
        Load();
        _address.Text = Settings.Tv.AdbAddress ?? "";
        SetStep(1);
        ShowView(NeedsInstall);

        AttachedToVisualTree += (_, _) => Safe(OnAttached);
        DetachedFromVisualTree += (_, _) => Safe(OnDetached);
    }

    /// <summary>Nothing set up yet: the TV is off and no install has ever reached a TV.</summary>
    private bool NeedsInstall => !Settings.Tv.Enabled && string.IsNullOrWhiteSpace(Settings.Tv.AdbAddress);

    // ---- lifetime ------------------------------------------------------------------------------------------------

    public override void OnShown() => Safe(Load);

    protected override void OnHostStateChangedCore() => LoadTvState();

    private void OnAttached()
    {
        Host.TvFeed.Changed += OnTvFeedChanged;
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
            _window.PropertyChanged += OnWindowPropertyChanged;
        // A fresh visit: start from the view that fits the settings, unless this page's own install is still running.
        if (!_adbRunHere)
        {
            _installDone = false;
            ClearInstallMessages();
            _address.Text = Settings.Tv.AdbAddress ?? "";
            SetStep(1);
            ShowView(NeedsInstall);
        }
        LoadTvState(); // a TV may have connected while the page was away
    }

    private void OnDetached()
    {
        Host.TvFeed.Changed -= OnTvFeedChanged;
        if (_window is not null)
            _window.PropertyChanged -= OnWindowPropertyChanged;
        _window = null;
        StopCalibrating(); // the calibration pattern never outlives the page
    }

    /// <summary>The window hides to the tray (the old settings window closed): the calibration pattern goes off.</summary>
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty && e.NewValue is false)
            Safe(StopCalibrating);
    }

    private void StopCalibrating()
    {
        bool was = Loading;
        Loading = true;
        try
        {
            _calibrating.IsChecked = false;
        }
        finally
        {
            Loading = was;
        }
        Host.SetTvCalibrating(false);
    }

    // ---- loading (ported) -----------------------------------------------------------------------------------------

    private void WireEvents()
    {
        _enabled.IsCheckedChanged += (_, _) => _ = SetTvEnabledAsync();
        _modeCover.IsCheckedChanged += (_, _) => Safe(() => SelectTvMode(_modeCover));
        _modeStrip.IsCheckedChanged += (_, _) => Safe(() => SelectTvMode(_modeStrip));
        _textScale.ValueChanged += (_, _) =>
        {
            Safe(ShowTvSliderTexts);
            EditTv(t => t.TextScale = (int)Math.Round(_textScale.Value));
        };
        _boxOpacity.ValueChanged += (_, _) =>
        {
            Safe(ShowTvSliderTexts);
            EditTv(t => t.BoxOpacity = Math.Round(Math.Clamp(_boxOpacity.Value, 0, 1), 2));
        };
        _stripBottom.ValueChanged += (_, _) =>
        {
            Safe(ShowTvSliderTexts);
            EditTv(t => t.StripBottom = (int)Math.Round(_stripBottom.Value));
        };
        _calibScale.ValueChanged += (_, _) =>
        {
            Safe(ShowTvSliderTexts);
            EditTv(t => t.CalibScale = Math.Round(_calibScale.Value, 4));
        };
        _calibX.ValueChanged += (_, _) =>
        {
            Safe(ShowTvSliderTexts);
            EditTv(t => t.CalibOffsetX = Math.Round(_calibX.Value, 4));
        };
        _calibY.ValueChanged += (_, _) =>
        {
            Safe(ShowTvSliderTexts);
            EditTv(t => t.CalibOffsetY = Math.Round(_calibY.Value, 4));
        };
        _calibrating.IsCheckedChanged += (_, _) => Safe(() =>
        {
            if (!Loading)
                Host.SetTvCalibrating(_calibrating.IsChecked == true);
        });
    }

    /// <summary>Fills the page from the settings without writing back.</summary>
    private void Load()
    {
        var tv = Settings.Tv;
        bool was = Loading;
        Loading = true;
        try
        {
            _enabled.IsChecked = tv.Enabled;
            _modeCover.IsChecked = tv.Mode == TvMode.Cover;
            _modeStrip.IsChecked = tv.Mode == TvMode.Strip;
            _textScale.Value = Math.Clamp(tv.TextScale, 50, 200);
            _boxOpacity.Value = Math.Clamp(tv.BoxOpacity, 0, 1);
            _stripBottom.Value = Math.Clamp(tv.StripBottom, 0, 60);
            LoadTvCalibration();
        }
        finally
        {
            Loading = was;
        }
        ShowTvSliderTexts();
        ShowModeCards();
        LoadTvState();
    }

    private void LoadTvCalibration()
    {
        var tv = Settings.Tv;
        _calibScale.Value = double.IsNaN(tv.CalibScale) ? 1 : Math.Clamp(tv.CalibScale, 0.8, 1.2);
        _calibX.Value = double.IsNaN(tv.CalibOffsetX) ? 0 : Math.Clamp(tv.CalibOffsetX, -0.1, 0.1);
        _calibY.Value = double.IsNaN(tv.CalibOffsetY) ? 0 : Math.Clamp(tv.CalibOffsetY, -0.1, 0.1);
    }

    /// <summary>The on/off switch (an install may have turned it on), the connected TV, the hint and the ADB buttons.</summary>
    private void LoadTvState()
    {
        bool was = Loading;
        Loading = true;
        try
        {
            _enabled.IsChecked = Settings.Tv.Enabled;
        }
        finally
        {
            Loading = was;
        }

        var clients = Host.TvFeed.Clients().Distinct(StringComparer.Ordinal).ToList();
        bool connected = clients.Count > 0;
        string? address = ShownAddress(Settings.Tv.AdbAddress);
        if (connected)
        {
            _stateTitle.Text = Loc.Format("TvConnectedTo", address ?? string.Join(", ", clients));
            _stateSub.Text = Loc.Get("TvConnectedSub");
        }
        else
        {
            _stateTitle.Text = Loc.Get("TvNotConnected");
            _stateSub.Text = Loc.Get(Settings.Tv.Enabled ? "TvNotConnectedSub" : "TvOffSub");
        }
        _stateIcon.Data = connected ? TvGlyph : WarnGlyph;
        Tint(_stateIcon, StrokeIcon.ForegroundProperty, connected ? "OkBrush" : "WarnBrush");
        Tint(_stateIconBox, Border.BackgroundProperty, connected ? "OkBgBrush" : "WarnBgBrush");
        _changeTvText.Text = Loc.Get(string.IsNullOrWhiteSpace(Settings.Tv.AdbAddress) ? "TvInstallHeader" : "TvChangeTv");

        _localNetNote.IsVisible = Host.TvLocalNetworkBlocked;
        bool busy = Host.IsInstallingTvApp;
        _install.IsEnabled = !busy;
        _diag.IsEnabled = !busy;
        // ADB work this page did not start (the startup update, or a run from the old settings window).
        if (busy && !_adbRunHere)
            _busy.IsVisible = true;
        else if (!busy)
            _busy.IsVisible = false;
    }

    /// <summary>The saved ADB address as shown to the user: adb's default port is left out.</summary>
    private static string? ShownAddress(string? adbAddress)
    {
        if (string.IsNullOrWhiteSpace(adbAddress))
            return null;
        string suffix = ":" + DefaultAdbPort;
        return adbAddress.EndsWith(suffix, StringComparison.Ordinal) ? adbAddress[..^suffix.Length] : adbAddress;
    }

    /// <summary>Feed thread: only who is connected matters here; redrawn on the UI thread. Never throws.</summary>
    private void OnTvFeedChanged(object? sender, TvFeedEvent e)
    {
        if (e is not TvClientsChanged)
            return;
        try
        {
            Dispatcher.UIThread.Post(() => Safe(LoadTvState));
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings: TV state update failed: {ex.GetType().Name}"); // the dispatcher is shutting down
        }
    }

    // ---- edits (ported) -------------------------------------------------------------------------------------------

    private async Task SetTvEnabledAsync()
    {
        try
        {
            if (Loading)
                return;
            await Host.SetTvEnabledAsync(_enabled.IsChecked == true);
            Host.SaveSettings();
            LoadTvState();
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings: TV toggle failed: {ex.GetType().Name}"); // AppHost reports server start failures itself
        }
    }

    private void SelectTvMode(RadioButton button)
    {
        // On every IsChecked change, unchecks included: the newly checked card raises its event while the old one is
        // still checked, so only the old card's later (unchecked) event leaves the looks right.
        ShowModeCards();
        if (button.IsChecked != true)
            return;
        EditTv(t =>
        {
            if (_modeStrip.IsChecked == true)
                t.Mode = TvMode.Strip;
            else if (_modeCover.IsChecked == true)
                t.Mode = TvMode.Cover;
        });
    }

    /// <summary>A TV setting edit: pushed to the TV at once (no save per slider tick), saved debounced.</summary>
    private void EditTv(Action<TvSettings> edit, Action? after = null) => Safe(() =>
    {
        if (Loading)
            return;
        edit(Settings.Tv);
        Host.PushTvConfig();
        ScheduleSave();
        after?.Invoke();
    });

    private void ResetTvCalibration()
    {
        var tv = Settings.Tv;
        tv.CalibScale = 1.0;
        tv.CalibOffsetX = 0;
        tv.CalibOffsetY = 0;
        bool was = Loading;
        Loading = true;
        try
        {
            LoadTvCalibration();
        }
        finally
        {
            Loading = was;
        }
        ShowTvSliderTexts();
        Host.PushTvConfig();
        ScheduleSave();
    }

    /// <summary>"จัดตำแหน่งบนทีวี": opens "ขั้นสูง" and turns the calibration pattern on (the old calibrate box).</summary>
    private void StartCalibrating()
    {
        SetAdvancedOpen(true);
        if (_calibrating.IsChecked == true)
            Host.SetTvCalibrating(true);
        else
            _calibrating.IsChecked = true; // its handler turns the pattern on
    }

    private void SetAdvancedOpen(bool open)
    {
        _advancedBody.IsVisible = open;
        _advancedChevron.Data = open ? ChevronUp : ChevronDown;
    }

    private void ShowModeCards()
    {
        bool strip = _modeStrip.IsChecked == true;
        _coverParts.Show(_modeCover.IsChecked == true);
        _stripParts.Show(strip);
        // The distance from the bottom edge applies to the strip only.
        _stripBottomRow.IsEnabled = strip;
        _stripBottomRow.Opacity = strip ? 1 : 0.55;
    }

    private void ShowTvSliderTexts()
    {
        var c = CultureInfo.InvariantCulture;
        _textScaleText.Text = Math.Round(_textScale.Value).ToString("0", c) + "%";
        _boxOpacityText.Text = (Math.Clamp(_boxOpacity.Value, 0, 1) * 100).ToString("0", c) + "%";
        _stripBottomText.Text = Math.Round(_stripBottom.Value).ToString("0", c) + "%";
        _calibScaleText.Text = (_calibScale.Value * 100).ToString("0.0", c) + "%";
        _calibXText.Text = (_calibX.Value * 100).ToString("+0.00;-0.00;0", c) + "%";
        _calibYText.Text = (_calibY.Value * 100).ToString("+0.00;-0.00;0", c) + "%";
    }

    // ---- views and stepper ----------------------------------------------------------------------------------------

    /// <summary>"เปลี่ยนทีวี" / "ติดตั้งแอปบนทีวี": the stepper from step 1 (an install of this page's keeps its place).</summary>
    private void OpenInstaller()
    {
        if (!_adbRunHere)
        {
            _installDone = false;
            ClearInstallMessages();
            // Replacing the TV: search the LAN by default instead of reinstalling on the saved one.
            _address.Text = "";
            SetStep(1);
        }
        ShowView(install: true);
    }

    private void ShowView(bool install)
    {
        _mainHeader.IsVisible = !install;
        _stateCard.IsVisible = !install;
        _modeCard.IsVisible = !install;
        _sizeCard.IsVisible = !install;
        _calibCard.IsVisible = !install;
        _advanced.IsVisible = !install;
        _installHeader.IsVisible = install;
        _stepper.IsVisible = install;
    }

    /// <summary>Lights the stepper: steps before <paramref name="active"/> done, it active, later ones pending.</summary>
    private void SetStep(int active)
    {
        _activeStep = active;
        for (int i = 0; i < _steps.Length; i++)
        {
            int n = i + 1;
            var state = _installDone || n < active ? StepState.Done : n == active ? StepState.Active : StepState.Pending;
            _steps[i].Show(state, showBody: n == active);
        }
        _doneNote.IsVisible = _installDone;
        _finish.IsVisible = _installDone;
    }

    /// <summary>The stepper step a <see cref="TvInstallStep"/> belongs to.</summary>
    private static int StepOf(TvInstallStep step) => step switch
    {
        TvInstallStep.Downloading or TvInstallStep.Finding => 2,
        TvInstallStep.Connecting or TvInstallStep.WaitingForApproval => 3,
        _ => 4, // Installing, Enabling, Done
    };

    private void ShowInstallStep(TvInstallStep step)
    {
        int n = StepOf(step);
        if (n != _activeStep)
            SetStep(n);
        foreach (var s in _stepStatus)
            s.IsVisible = false;
        var text = _stepStatus[n - 1];
        text.Text = Loc.Get("TvInstallStep" + step);
        text.IsVisible = true;
    }

    private void ClearInstallMessages()
    {
        _installError.IsVisible = false;
        foreach (var s in _stepStatus)
            s.IsVisible = false;
    }

    // ---- install and diagnostics (ported) -------------------------------------------------------------------------

    /// <summary>
    /// The typed TV address as an adb endpoint: blank = find the TV on the LAN (null); a bare IP gets adb's default
    /// port. False when it is not a valid <c>host:port</c>.
    /// </summary>
    private static bool TryParseTvAddress(string? text, out string? endpoint)
    {
        endpoint = null;
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return true;
        if (!trimmed.Contains(':', StringComparison.Ordinal))
            trimmed += ":" + DefaultAdbPort;
        if (!AdbClient.IsValidEndpoint(trimmed))
            return false;
        endpoint = trimmed;
        return true;
    }

    /// <summary>
    /// Installs the TV app through <see cref="AppHost.InstallTvAppAsync"/> (which saves the address and turns the TV on
    /// when it works), lighting the stepper from each step and showing the final error. The button stays disabled while
    /// it runs.
    /// </summary>
    private async Task InstallTvAppAsync()
    {
        try
        {
            if (Host.IsInstallingTvApp)
                return;
            _installDone = false;
            ClearInstallMessages();
            SetStep(2);
            if (!TryParseTvAddress(_address.Text, out var endpoint))
            {
                ShowInstallError("TvAddressInvalid");
                return;
            }
            if (endpoint is not null)
                _address.Text = endpoint;

            _install.IsEnabled = false;
            // Progress<T> posts to the UI thread (captured here).
            var progress = new Progress<TvInstallStep>(step => Safe(() => ShowInstallStep(step)));
            TvInstallResult result;
            _adbRunHere = true;
            try
            {
                result = await Host.InstallTvAppAsync(endpoint, progress);
            }
            finally
            {
                _adbRunHere = false;
            }
            if (result.Ok)
            {
                ClearInstallMessages();
                _installDone = true;
                SetStep(4);
                bool was = Loading;
                Loading = true;
                try
                {
                    _address.Text = Settings.Tv.AdbAddress ?? "";
                }
                finally
                {
                    Loading = was;
                }
            }
            else
            {
                ClearInstallMessages();
                SetStep(2);
                ShowInstallError(result.ErrorKey ?? "TvInstallFailed");
            }
            LoadTvState();
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings: TV install failed: {ex.GetType().Name}");
            Safe(() =>
            {
                ClearInstallMessages();
                SetStep(2);
                ShowInstallError("TvInstallFailed");
                LoadTvState();
            });
        }
    }

    /// <summary>
    /// Collects the TV diagnostics zip through <see cref="AppHost.CollectTvDiagnosticsAsync"/>, showing each step, then
    /// the zip's file name (and revealing it) or the error. Both TV buttons stay disabled while it runs.
    /// </summary>
    private async Task CollectTvDiagnosticsAsync()
    {
        try
        {
            if (Host.IsInstallingTvApp)
                return;
            _diagError.IsVisible = false;
            _installError.IsVisible = false;
            // A blank box (e.g. after "เปลี่ยนทีวี") falls back to the saved TV, as the old tab's prefilled box did.
            bool typed = !string.IsNullOrWhiteSpace(_address.Text);
            if (!TryParseTvAddress(typed ? _address.Text : Settings.Tv.AdbAddress, out var endpoint))
            {
                ShowDiagError("TvAddressInvalid");
                return;
            }
            if (typed && endpoint is not null)
                _address.Text = endpoint;

            _diag.IsEnabled = false;
            _install.IsEnabled = false;
            _diagStep.IsVisible = false;
            // Progress<T> posts to the UI thread (captured here).
            var progress = new Progress<TvDiagStep>(step => Safe(() =>
            {
                _diagStep.Text = Loc.Get("TvDiagStep" + step);
                _diagStep.IsVisible = true;
            }));
            TvDiagResult result;
            _adbRunHere = true;
            try
            {
                result = await Host.CollectTvDiagnosticsAsync(endpoint, progress);
            }
            finally
            {
                _adbRunHere = false;
            }
            if (result.Ok && result.ZipPath is { } zip)
            {
                _diagStep.Text = Loc.Format("TvDiagDone", System.IO.Path.GetFileName(zip));
                _diagStep.IsVisible = true;
                FileReveal.Reveal(zip);
            }
            else
            {
                _diagStep.IsVisible = false;
                ShowDiagError(result.ErrorKey ?? "TvDiagWriteFailed");
            }
            LoadTvState();
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings: TV diagnostics failed: {ex.GetType().Name}");
            Safe(() =>
            {
                ShowDiagError("TvDiagWriteFailed");
                LoadTvState();
            });
        }
    }

    private void ShowDiagError(string key)
    {
        _diagErrorText.Text = Loc.Get(key);
        _diagError.IsVisible = true;
    }

    private void ShowInstallError(string key)
    {
        _installErrorText.Text = Loc.Get(key);
        _installError.IsVisible = true;
    }

    // ---- builders -------------------------------------------------------------------------------------------------

    /// <summary>Step 1: the developer-mode instructions and, beside them (below when narrow), the "press OK 7 times" guide.</summary>
    private static Control Step1Body()
    {
        var lines = new StackPanel { Spacing = 12 };
        lines.Children.Add(InstructionLine(1, Words(Loc.Get("TvStep1Line1")), Chip(Loc.Get("TvChipSettings")), Arrow(), Chip(Loc.Get("TvChipSystem")), Arrow(), Chip(Loc.Get("TvChipAbout"))));
        lines.Children.Add(InstructionLine(2, Words(Loc.Get("TvStep1Line2")), Chip(Loc.Get("TvChipBuild"))));
        var press = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        press.Inlines = new InlineCollection
        {
            new Run(Loc.Get("TvStep1Line3a") + " "),
            new Run(Loc.Get("TvStep1Line3Bold")) { FontWeight = FontWeight.Bold },
            new Run(" " + Loc.Get("TvStep1Line3b")),
        };
        lines.Children.Add(InstructionLine(3, press));
        lines.Children.Add(InstructionLine(4, Words(Loc.Get("TvStep1Line4")), Chip(Loc.Get("TvChipSystem")), Arrow(), Chip(Loc.Get("TvChipDevOptions"))));
        lines.Children.Add(InstructionLine(5, Words(Loc.Get("TvStep1Line5")), Chip(Loc.Get("TvChipUsbDebug")),
            new TextBlock { Classes = { "sub" }, Text = Loc.Get("TvStep1And"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) },
            Chip(Loc.Get("TvChipNetDebug"))));

        var guide = Guide();
        const double guideWidth = 300, gap = 24, minLines = 280;
        var grid = new Grid();
        grid.Children.Add(lines);
        grid.Children.Add(guide);
        bool? wide = null;
        void Apply(bool w)
        {
            if (wide == w)
                return;
            wide = w;
            if (w)
            {
                grid.ColumnDefinitions = new ColumnDefinitions { new(GridLength.Star), new(guideWidth, GridUnitType.Pixel) };
                grid.RowDefinitions = new RowDefinitions("Auto");
                grid.ColumnSpacing = gap;
                grid.RowSpacing = 0;
                Grid.SetColumn(guide, 1);
                Grid.SetRow(guide, 0);
                guide.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
            else
            {
                grid.ColumnDefinitions = new ColumnDefinitions("*");
                grid.RowDefinitions = new RowDefinitions("Auto,Auto");
                grid.ColumnSpacing = 0;
                grid.RowSpacing = 16;
                Grid.SetColumn(guide, 0);
                Grid.SetRow(guide, 1);
                guide.HorizontalAlignment = HorizontalAlignment.Left;
            }
        }
        Apply(true);
        // Only the width decides, and switching changes only the height, so this cannot oscillate.
        grid.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
                Apply(e.NewSize.Width - guideWidth - gap >= minLines);
        };
        return grid;
    }

    /// <summary>A small TV screen with the "About" list, the build row highlighted, and the caption below.</summary>
    private static Control Guide()
    {
        static TextBlock Row(string text, bool on)
        {
            var block = new TextBlock { Text = text, FontSize = 12, LineHeight = 18, Foreground = new SolidColorBrush(Color.Parse(on ? "#111111" : "#d8e0ee")) };
            if (on)
                block.FontWeight = FontWeight.SemiBold;
            return block;
        }
        static Border Line(string text, bool on) => new()
        {
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(4),
            Background = on ? Brushes.White : Brushes.Transparent,
            Child = Row(text, on),
        };
        var screen = new Border
        {
            Width = 300,
            Height = 169,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(4),
            BorderBrush = new SolidColorBrush(Color.Parse("#0d0f12")),
            Background = new SolidColorBrush(Color.Parse("#1a2230")),
            Padding = new Thickness(14),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = Loc.Get("TvChipAbout"), FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White },
                    Line(Loc.Get("TvGuideModel"), false),
                    Line(Loc.Get("TvGuideOsVersion"), false),
                    Line(Loc.Get("TvChipBuild"), true),
                },
            },
        };
        return new StackPanel
        {
            Spacing = 8,
            Width = 300,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { screen, new TextBlock { Classes = { "sub" }, Text = Loc.Get("TvGuideCaption"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap } },
        };
    }

    private static Control InstructionLine(int number, params Control[] parts)
    {
        var marker = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = number.ToString(CultureInfo.InvariantCulture), FontSize = 13, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        Tint(marker, Border.BackgroundProperty, "Card2Brush");
        Tint(marker, Border.BorderBrushProperty, "Border2Brush");
        Control wrap;
        if (parts.Length == 1)
        {
            // A single (wrapping) text: a WrapPanel would measure it unbounded and it could not wrap.
            wrap = parts[0];
            wrap.Margin = new Thickness(0, 1, 0, 0);
        }
        else
        {
            var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, -6) };
            foreach (var part in parts)
            {
                if (part.Margin == default)
                    part.Margin = new Thickness(0, 0, 8, 6);
                panel.Children.Add(part);
            }
            wrap = panel;
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*"), ColumnSpacing = 12 };
        grid.Children.Add(marker);
        Grid.SetColumn(wrap, 1);
        grid.Children.Add(wrap);
        return grid;
    }

    private static TextBlock Words(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    private static Border Chip(string text)
    {
        var chip = new Border
        {
            Padding = new Thickness(10, 2),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 14 },
        };
        Tint(chip, Border.BackgroundProperty, "Card2Brush");
        Tint(chip, Border.BorderBrushProperty, "Border2Brush");
        return chip;
    }

    private static StrokeIcon Arrow()
    {
        var icon = new StrokeIcon { Data = ChevronRight, Size = 16, VerticalAlignment = VerticalAlignment.Center };
        Tint(icon, StrokeIcon.ForegroundProperty, "Text3Brush");
        return icon;
    }

    /// <summary>A labelled slider with its value in a pill (mockup grid 200 | slider | 64).</summary>
    private static Grid SliderRow(string title, string? sub, Slider slider, TextBlock value)
    {
        var label = FormLabel(title, sub);
        if (label.Children.Count > 1 && label.Children[1] is TextBlock s)
            s.FontSize = 13;
        slider.VerticalAlignment = VerticalAlignment.Center;
        Avalonia.Automation.AutomationProperties.SetName(slider, title);
        value.HorizontalAlignment = HorizontalAlignment.Center;
        var pill = new Border { Classes = { "pill", "info" }, Child = value, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*,64"), ColumnSpacing = 16 };
        row.Children.Add(label);
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        Grid.SetColumn(pill, 2);
        row.Children.Add(pill);
        return row;
    }

    /// <summary>The radio ring and dot of a mode card, which follow its checked state.</summary>
    private sealed class RadioParts(Ellipse ring, Ellipse dot)
    {
        public void Show(bool on)
        {
            ring.Bind(Shape.StrokeProperty, ring.GetResourceObservable(on ? "AccentBrush" : "Text3Brush"));
            dot.IsVisible = on;
        }
    }

    private static RadioParts ModeCard(RadioButton button, bool cover, string title, string sub)
    {
        var ring = new Ellipse { StrokeThickness = 2 };
        var dot = new Ellipse { Width = 10, Height = 10, IsVisible = false };
        dot.Bind(Shape.FillProperty, dot.GetResourceObservable("AccentBrush"));
        var radio = new Panel { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0), Children = { ring, dot } };
        var text = new StackPanel
        {
            Children =
            {
                new TextBlock { Classes = { "lbl" }, Text = title, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Classes = { "sub" }, FontSize = 14, Text = sub, TextWrapping = TextWrapping.Wrap },
            },
        };
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        line.Children.Add(radio);
        Grid.SetColumn(text, 1);
        line.Children.Add(text);
        button.Content = new StackPanel { Spacing = 12, Children = { ModePreview(cover), line } };
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Stretch;
        return new RadioParts(ring, dot);
    }

    /// <summary>The mockup's little TV picture: a scene with the English line, covered by a box or with a strip below.</summary>
    private static Control ModePreview(bool cover)
    {
        static Border Block(double left, double top, double w, double h, Color fill, double radius = 0) => new()
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(left, top, 0, 0),
            Width = w,
            Height = h,
            Background = new SolidColorBrush(fill),
            CornerRadius = new CornerRadius(radius),
        };
        static Color C(string hex) => Color.Parse(hex);
        var scene = new Grid { Width = 174, Height = 95, ClipToBounds = true };
        scene.Children.Add(Block(0, 0, 174, 50, C("#22344a")));
        scene.Children.Add(Block(0, 50, 174, 45, C("#23281f")));
        scene.Children.Add(Block(18, 25, 24, 25, C("#1d2630")));
        scene.Children.Add(Block(126, 18, 32, 32, C("#18212a")));
        scene.Children.Add(Block(49, 29, 17, 21, C("#202c38")));
        scene.Children.Add(Block(78, 36, 11, 21, C("#0f141a"), 3));
        scene.Children.Add(Block(98, 39, 9, 18, C("#141a20"), 3));
        scene.Children.Add(Block(47, 60, 80, 3, Color.FromArgb(153, 255, 255, 255), 2)); // the game's English line
        if (cover)
        {
            scene.Children.Add(Block(31, 55, 112, 12, Color.FromArgb(191, 0, 0, 0), 3));
            scene.Children.Add(Block(41, 59, 92, 4, Colors.White, 2));
        }
        else
        {
            scene.Children.Add(Block(0, 69, 174, 26, Color.FromArgb(217, 0, 0, 0)));
            scene.Children.Add(Block(41, 81, 92, 4, Colors.White, 2));
        }
        return new Border
        {
            Width = 180,
            Height = 101,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(3),
            BorderBrush = new SolidColorBrush(C("#0d0f12")),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = scene,
        };
    }

    /// <summary>One stepper row: the round marker, title and sub, and the body shown while it is the active step.</summary>
    private sealed class StepRow
    {
        private readonly Border _marker = new() { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Top };
        private readonly TextBlock _number = new() { FontSize = 14, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly StrokeIcon _check = new() { Data = CheckGlyph, Size = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _title;
        private readonly TextBlock _sub;
        private readonly Control _body;
        private readonly StackPanel _content;

        public StepRow(int number, string title, string sub, Control body)
        {
            _number.Text = number.ToString(CultureInfo.InvariantCulture);
            _marker.Child = new Panel { Children = { _number, _check } };
            _title = new TextBlock { Classes = { "lbl" }, Text = title, TextWrapping = TextWrapping.Wrap };
            _sub = new TextBlock { Classes = { "sub" }, Text = sub, TextWrapping = TextWrapping.Wrap };
            _body = body;
            _content = new StackPanel { Spacing = 14, Children = { new StackPanel { Children = { _title, _sub } }, _body } };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16 };
            grid.Children.Add(_marker);
            Grid.SetColumn(_content, 1);
            grid.Children.Add(_content);
            Root = new Border { Child = grid };
        }

        public Border Root { get; }

        public void Show(StepState state, bool showBody)
        {
            bool active = state == StepState.Active;
            _body.IsVisible = showBody;
            _title.FontSize = active ? 17 : 15;
            _sub.FontSize = active ? 14 : 13;
            Root.Padding = active ? new Thickness(22, 20) : new Thickness(22, 16);
            Root.Opacity = state == StepState.Pending ? 0.7 : 1;
            if (active)
                Tint(Root, Border.BackgroundProperty, "Card2Brush");
            else
                Root.ClearValue(Border.BackgroundProperty);

            _number.IsVisible = state != StepState.Done;
            _check.IsVisible = state == StepState.Done;
            _marker.BorderThickness = state == StepState.Pending ? new Thickness(2) : new Thickness(0);
            switch (state)
            {
                case StepState.Active:
                    Tint(_marker, Border.BackgroundProperty, "AccentBrush");
                    Tint(_number, TextBlock.ForegroundProperty, "OnAccentBrush");
                    break;
                case StepState.Done:
                    Tint(_marker, Border.BackgroundProperty, "OkBgBrush");
                    Tint(_check, StrokeIcon.ForegroundProperty, "OkBrush");
                    break;
                default:
                    _marker.ClearValue(Border.BackgroundProperty);
                    Tint(_marker, Border.BorderBrushProperty, "Border2Brush");
                    Tint(_number, TextBlock.ForegroundProperty, "Text3Brush");
                    break;
            }
        }
    }

    private static StrokeIcon DoneIcon()
    {
        var icon = new StrokeIcon { Data = CheckGlyph, Size = 18, VerticalAlignment = VerticalAlignment.Center };
        Tint(icon, StrokeIcon.ForegroundProperty, "OkBrush");
        return icon;
    }

    private static Border ErrorNote(TextBlock text)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        grid.Children.Add(new StrokeIcon { Data = WarnGlyph, Size = 18, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return new Border { Classes = { "note", "bad" }, IsVisible = false, Child = grid };
    }

    private static Control WithMargin(Control child, Thickness margin)
    {
        child.Margin = margin;
        return child;
    }

    private static void Tint(AvaloniaObject target, AvaloniaProperty property, string brushKey)
    {
        if (target is Control control)
            control.Bind(property, control.GetResourceObservable(brushKey));
    }

    private static Button IconButton(string text, string? glyph, string classes, Action action)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (glyph is not null)
            row.Children.Add(new StrokeIcon { Data = glyph, Size = 16, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Left };
        button.Classes.AddRange(classes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        button.Click += (_, _) => Safe(action);
        return button;
    }
}
