using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App.Views.Pages;

/// <summary>
/// Base of every settings page hosted in the main window. Handles the debounced save, the active-profile edit helper,
/// the load guard and the show/leave hooks. UI thread only.
/// </summary>
public abstract class SettingsPage : UserControl
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500); // same as the old settings window

    private readonly DispatcherTimer _saveTimer;

    protected SettingsPage(AppHost host)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        _saveTimer = new DispatcherTimer { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) => Safe(FlushSave);
        AttachedToVisualTree += (_, _) => host.StateChanged += OnHostStateChanged;
        DetachedFromVisualTree += (_, _) => host.StateChanged -= OnHostStateChanged;
    }

    protected AppHost Host { get; }

    protected AppSettings Settings => Host.Settings;

    /// <summary>True while the page fills its controls from settings; change handlers must ignore events then.</summary>
    protected bool Loading { get; set; }

    /// <summary>Reloads the controls from settings. Called each time the page is shown and when the active game changes.</summary>
    public virtual void OnShown()
    {
    }

    /// <summary>Commits half-typed fields. Called before leaving the page, on window hide/close and on game switch.</summary>
    public virtual void CommitPending()
    {
    }

    /// <summary>Host state changed (not called on page detach). Default: nothing; pages override to refresh status.</summary>
    protected virtual void OnHostStateChangedCore()
    {
    }

    private void OnHostStateChanged(object? sender, EventArgs e) => Safe(OnHostStateChangedCore);

    /// <summary>Restarts the save debounce; the settings are written <see cref="SaveDelay"/> after the last call.</summary>
    protected void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes pending changes now (no-op when nothing is pending).</summary>
    public void FlushSave()
    {
        if (!_saveTimer.IsEnabled)
            return;
        _saveTimer.Stop();
        Host.SaveSettings();
    }

    /// <summary>Applies an edit to the active game, pushes it to the running app, and schedules a save. Ignored while loading.</summary>
    protected void EditActive(Action<GameProfile> edit, Action? after = null) => Safe(() =>
    {
        if (Loading)
            return;
        edit(Settings.GetActive());
        Host.ApplySettings();
        ScheduleSave();
        after?.Invoke();
    });

    /// <summary>Runs a UI handler; a failure is logged (type only: a message could echo a typed key or URL) and swallowed.</summary>
    protected static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings: control handler failed: {ex.GetType().Name}");
        }
    }

    /// <summary>Calls <paramref name="commit"/> when the box loses focus or Enter is pressed.</summary>
    protected static void CommitOn(TextBox box, Action commit)
    {
        box.LostFocus += (_, _) => Safe(commit);
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            Safe(commit);
        };
    }

    /// <summary>Page header: title (h1) and a muted subtitle line.</summary>
    protected static Control PageHeader(string title, string subtitle) => new StackPanel
    {
        Spacing = 4,
        Margin = new Thickness(0, 0, 0, 18),
        Children =
        {
            new TextBlock { Text = title, Classes = { "h1" } },
            new TextBlock { Text = subtitle, Classes = { "mut" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
        },
    };

    /// <summary>Heading of a section: h2 title and an optional sub line.</summary>
    protected static Control SectionHeader(string title, string? sub = null)
    {
        var panel = new StackPanel { Spacing = 2, Children = { new TextBlock { Text = title, Classes = { "h2" } } } };
        if (!string.IsNullOrEmpty(sub))
            panel.Children.Add(new TextBlock { Text = sub, Classes = { "sub" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        return panel;
    }

    /// <summary>A form label: the title (semibold, wraps) and an optional sub line (14 px, muted).</summary>
    protected static StackPanel FormLabel(string title, string? sub = null)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Classes = { "lbl" }, Text = title, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(sub))
            stack.Children.Add(new TextBlock { Classes = { "sub" }, Text = sub, FontSize = 14, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        return stack;
    }

    /// <summary>
    /// A responsive form row as in the mockups (label | control). While the row is wide enough for the label to keep at
    /// least <paramref name="minLabelWidth"/>, the label sits on the left and <paramref name="control"/> in a fixed
    /// <paramref name="controlWidth"/> column on the right (24 px apart); narrower, the label stacks above the control,
    /// which then takes the full width. Example: <c>FormRow(Loc.Get("GamesTitleLabel"), Loc.Get("GamesTitleSub"), box)</c>.
    /// </summary>
    protected static Grid FormRow(string title, string? sub, Control control, double controlWidth = 300, double minLabelWidth = 220)
    {
        const double gap = 24;
        var label = FormLabel(title, sub);
        var grid = new Grid();
        grid.Children.Add(label);
        grid.Children.Add(control);
        control.VerticalAlignment = VerticalAlignment.Center;
        bool? wide = null;
        void Apply(bool w)
        {
            if (wide == w)
                return;
            wide = w;
            if (w)
            {
                grid.ColumnDefinitions = new ColumnDefinitions { new(GridLength.Star), new(controlWidth, GridUnitType.Pixel) };
                grid.RowDefinitions = new RowDefinitions("Auto");
                grid.ColumnSpacing = gap;
                grid.RowSpacing = 0;
                Grid.SetColumn(control, 1);
                Grid.SetRow(control, 0);
            }
            else
            {
                grid.ColumnDefinitions = new ColumnDefinitions("*");
                grid.RowDefinitions = new RowDefinitions("Auto,Auto");
                grid.ColumnSpacing = 0;
                grid.RowSpacing = 10;
                Grid.SetColumn(control, 0);
                Grid.SetRow(control, 1);
            }
        }
        Apply(true);
        // Only the width decides, and switching changes only the height, so this cannot oscillate.
        grid.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
                Apply(e.NewSize.Width - controlWidth - gap >= minLabelWidth);
        };
        return grid;
    }

    /// <summary>
    /// A toggle row: <see cref="FormLabel"/> plus optional <paramref name="extra"/> content (a note, a text box) on the
    /// left, the switch (ToggleSwitch.tg) top-right.
    /// </summary>
    protected static Grid FormToggleRow(string title, string? sub, ToggleSwitch toggle, Control? extra = null)
    {
        var left = FormLabel(title, sub);
        if (extra is not null)
            left.Children.Add(extra);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        grid.Children.Add(left);
        toggle.VerticalAlignment = VerticalAlignment.Top;
        toggle.Margin = new Thickness(0, 2, 0, 0);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return grid;
    }

    /// <summary>A section card (Border.section) with heading and body.</summary>
    protected static Border Section(string title, string? sub, Control body) => new()
    {
        Classes = { "section" },
        Child = new StackPanel { Spacing = 16, Children = { SectionHeader(title, sub), body } },
    };

    /// <summary>
    /// Sets the page content to <paramref name="body"/> inside a vertical ScrollViewer with the standard page margins
    /// (32,4,32,28, as Home). Pages built in XAML may do the same layout themselves.
    /// </summary>
    protected void SetBody(Control body)
    {
        body.Margin = new Thickness(32, 4, 32, 28);
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = body,
        };
    }

    /// <summary>A vertical page body (scrollable by the host); sections are spaced 16 px apart.</summary>
    protected static StackPanel PageBody(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var child in children)
            panel.Children.Add(child);
        return panel;
    }
}
