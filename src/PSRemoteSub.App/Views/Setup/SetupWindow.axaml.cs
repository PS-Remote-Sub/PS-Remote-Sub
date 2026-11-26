using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PSRemoteSub.App.Views.Controls;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App.Views.Setup;

/// <summary>
/// The first-run setup wizard (mockups New-Setup-1 … 4): picture source → game language → translator → subtitle
/// position. "ถัดไป" writes the shown step (<see cref="SetupStep.Apply"/>) and moves on; "ย้อนกลับ" moves back without
/// writing; "ข้ามไปก่อน" and "เสร็จแล้ว ไปหน้าหลัก" mark setup done, save, raise <see cref="Finished"/> (completed) and
/// close; X raises <see cref="Finished"/> (not completed) without marking it done. UI thread only.
/// </summary>
public partial class SetupWindow : Window
{
    private const string SunIcon = "M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM12 2.5v2M12 19.5v2M4.6 4.6l1.4 1.4M18 18l1.4 1.4M2.5 12h2M19.5 12h2M4.6 19.4 6 18M18 6l1.4-1.4";
    private const string MoonIcon = "M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z";
    private const string CheckGlyph = "M5 12.5l4.5 4.5L19 7.5";

    private readonly AppHost? _host;
    private readonly SetupStep[] _steps = [];
    private int _index = -1;
    private bool _finished; // Finished was raised (or the app is exiting): closing raises nothing more

    /// <summary>For the XAML loader and designer only.</summary>
    public SetupWindow() => InitializeComponent();

    public SetupWindow(AppHost host) : this()
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _steps = [new SourceStep(host), new LanguageStep(host), new TranslatorStep(host), new RegionStep(host)];
        BackButton.Click += (_, _) => Safe(() => ShowStep(_index - 1));
        NextButton.Click += (_, _) => Safe(Next);
        SkipButton.Click += (_, _) => Safe(() => Finish(completed: true));
        ThemeButton.Click += (_, _) => Safe(ToggleTheme);
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += OnThemeChanged;
        Closed += (_, _) =>
        {
            if (Application.Current is { } a)
                a.ActualThemeVariantChanged -= OnThemeChanged;
            if (!_finished)
                Finish(completed: false); // X: not marked done
        };
        UpdateThemeButton();
        ShowStep(0);
    }

    /// <summary>Raised once: when the wizard is completed or skipped, or closed with X (see <see cref="SetupFinishedEventArgs"/>).</summary>
    public event EventHandler<SetupFinishedEventArgs>? Finished;

    private AppHost Host => _host ?? throw new InvalidOperationException("No host");

    /// <summary>Number of steps (4).</summary>
    public int StepCount => _steps.Length;

    /// <summary>Shows step <paramref name="index"/> (0-based, clamped): stepper, title block, content and footer.</summary>
    public void ShowStep(int index)
    {
        if (_steps.Length == 0)
            return;
        index = Math.Clamp(index, 0, _steps.Length - 1);
        var step = _steps[index];
        _index = index;
        BuildStepper();
        StepOf.Text = Loc.Format("SetupStepOf", index + 1, _steps.Length);
        TitleText.Text = step.Title;
        SubText.Text = step.Subtitle;
        StepHost.Content = step;
        BackButton.IsVisible = index > 0;
        NextText.Text = Loc.Get(index == _steps.Length - 1 ? "SetupFinish" : "SetupNext");
        Scroller.Offset = default;
        Safe(step.OnShown);
    }

    /// <summary>Closes for app exit without raising <see cref="Finished"/>.</summary>
    internal void CloseForExit()
    {
        _finished = true;
        Close();
    }

    private void Next()
    {
        if (_index < 0)
            return;
        try
        {
            _steps[_index].Apply();
        }
        catch (Exception ex)
        {
            Log.Warn($"Setup: applying step {_index + 1} failed: {ex.GetType().Name}"); // the wizard still moves on
        }
        if (_index == _steps.Length - 1)
            Finish(completed: true);
        else
            ShowStep(_index + 1);
    }

    /// <summary>
    /// Ends the wizard once. Completed (done or skipped): marks setup done and saves first. Then raises
    /// <see cref="Finished"/> and, when completed, closes the window (X already closed it).
    /// </summary>
    private void Finish(bool completed)
    {
        if (_finished)
            return;
        _finished = true;
        var next = MainPage.Home;
        try
        {
            if (completed)
            {
                Host.Settings.SetupDone = true;
                Host.SaveSettings();
                var t = Host.Settings.Translation;
                if (t.Mode == TranslationMode.Ai && !t.HasAiSetUp())
                    next = MainPage.Translation; // AI chosen but its key (or address/model) is missing: finish it there
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Setup: finishing failed: {ex.GetType().Name}");
        }
        try
        {
            Finished?.Invoke(this, new SetupFinishedEventArgs(completed, next));
        }
        catch (Exception ex)
        {
            Log.Warn("Setup: a Finished handler failed", ex);
        }
        if (completed)
            Close();
    }

    // ---- stepper --------------------------------------------------------------------------------------------------

    /// <summary>Done steps: ok tint with a check; the current one: accent with its number; upcoming: outlined.</summary>
    private void BuildStepper()
    {
        Stepper.Children.Clear();
        Stepper.ColumnDefinitions.Clear();
        Stepper.ColumnSpacing = 12;
        for (int i = 0; i < _steps.Length; i++)
        {
            if (i > 0)
            {
                bool doneBefore = i - 1 < _index;
                var connector = new Border
                {
                    Height = 2,
                    CornerRadius = new CornerRadius(1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = doneBefore ? 0.6 : 1,
                };
                connector.Bind(Border.BackgroundProperty, connector.GetResourceObservable(doneBefore ? "OkBrush" : "Border2Brush"));
                Stepper.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                Grid.SetColumn(connector, Stepper.ColumnDefinitions.Count - 1);
                Stepper.Children.Add(connector);
            }
            var item = StepperItem(i);
            Stepper.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(item, Stepper.ColumnDefinitions.Count - 1);
            Stepper.Children.Add(item);
        }
    }

    private StackPanel StepperItem(int i)
    {
        bool done = i < _index;
        bool current = i == _index;
        var circle = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14) };
        if (done)
        {
            circle.Bind(Border.BackgroundProperty, circle.GetResourceObservable("OkBgBrush"));
            var check = new StrokeIcon { Data = CheckGlyph, Size = 16, StrokeThickness = 2.6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            check.Bind(StrokeIcon.ForegroundProperty, check.GetResourceObservable("OkBrush"));
            circle.Child = check;
        }
        else
        {
            var number = new TextBlock
            {
                Text = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (current)
            {
                circle.Bind(Border.BackgroundProperty, circle.GetResourceObservable("AccentBrush"));
                number.Bind(TextBlock.ForegroundProperty, number.GetResourceObservable("OnAccentBrush"));
            }
            else
            {
                circle.BorderThickness = new Thickness(2);
                circle.Bind(Border.BorderBrushProperty, circle.GetResourceObservable("Border2Brush"));
                number.Bind(TextBlock.ForegroundProperty, number.GetResourceObservable("Text3Brush"));
            }
            circle.Child = number;
        }
        var label = new TextBlock
        {
            Text = _steps[i].StepName,
            FontSize = 14,
            FontWeight = current ? FontWeight.SemiBold : FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable(current ? "TextBrush" : done ? "Text2Brush" : "Text3Brush"));
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { circle, label } };
    }

    // ---- theme (as the main window) -------------------------------------------------------------------------------

    private void ToggleTheme()
    {
        if (Application.Current is not { } app)
            return;
        ThemeApplier.Toggle(app, Host.Settings);
        Host.SaveSettings();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Safe(UpdateThemeButton);

    private void UpdateThemeButton()
    {
        bool light = Application.Current is { } app && ThemeApplier.IsLight(app);
        ThemeIcon.Data = light ? MoonIcon : SunIcon;
        ThemeText.Text = Loc.Get(light ? "ThemeToDark" : "ThemeToLight");
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn("Setup window action failed", ex); // a UI action must not take the app down
        }
    }
}
