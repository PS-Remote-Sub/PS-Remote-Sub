using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using PSRemoteSub.App.Views.Controls;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App.Views.Setup;

/// <summary>
/// One page of the setup wizard: its title block copy, its content, and what "ถัดไป" writes (<see cref="Apply"/>).
/// A step reads the settings once when built and keeps the user's choice while they move back and forth; only Next
/// writes it. UI thread only.
/// </summary>
public abstract class SetupStep : UserControl
{
    protected const string CheckGlyph = "M5 12.5l4.5 4.5L19 7.5";
    protected const string InfoGlyph = "M12 21.5a9.5 9.5 0 1 0 0-19 9.5 9.5 0 0 0 0 19zM12 11v5.5M12 7.8h.01";

    protected SetupStep(AppHost host)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        AttachedToVisualTree += (_, _) => host.StateChanged += OnHostStateChanged;
        DetachedFromVisualTree += (_, _) => host.StateChanged -= OnHostStateChanged;
    }

    protected AppHost Host { get; }

    protected AppSettings Settings => Host.Settings;

    /// <summary>The stepper label ("ภาพเกม" …).</summary>
    public abstract string StepName { get; }

    /// <summary>The h1 of the title block.</summary>
    public abstract string Title { get; }

    /// <summary>The muted line under the title.</summary>
    public abstract string Subtitle { get; }

    /// <summary>Called each time the step is shown: refreshes what the app knows (status pills, availability).</summary>
    public virtual void OnShown()
    {
    }

    /// <summary>"ถัดไป" / "เสร็จแล้ว": writes the step's choice through the same AppHost path as the settings pages.</summary>
    public virtual void Apply()
    {
    }

    /// <summary>Host state changed while the step is shown.</summary>
    protected virtual void OnHostStateChangedCore()
    {
    }

    private void OnHostStateChanged(object? sender, EventArgs e) => Safe(OnHostStateChangedCore);

    /// <summary>Runs a UI handler; a failure is logged (type only) and swallowed.</summary>
    protected static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn($"Setup: handler failed: {ex.GetType().Name}");
        }
    }

    // ---- building blocks ------------------------------------------------------------------------------------------

    /// <summary>
    /// An option card (RadioButton.opt): its radio ring, and optionally an icon box, follow the button's own IsChecked.
    /// Call <see cref="Show"/> for every card of a group on every IsCheckedChanged (Avalonia raises the newly checked
    /// one while the old one is still checked).
    /// </summary>
    protected sealed class OptionCard
    {
        private readonly Ellipse _ring = new() { StrokeThickness = 2 };
        private readonly Ellipse _dot = new() { Width = 10, Height = 10, IsVisible = false };
        private Border? _iconBox;
        private StrokeIcon? _icon;

        public OptionCard(string group)
        {
            Button = new RadioButton
            {
                Classes = { "opt" },
                GroupName = group,
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            _dot.Bind(Shape.FillProperty, _dot.GetResourceObservable("AccentBrush"));
            Radio = new Panel { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center, Children = { _ring, _dot } };
        }

        public RadioButton Button { get; }

        /// <summary>The 20 px radio ring to place in the card's content.</summary>
        public Panel Radio { get; }

        /// <summary>A 48 px icon box (info tint when checked, Card2 otherwise) to place in the card's content.</summary>
        public Border IconBox(string glyph)
        {
            _icon = new StrokeIcon { Data = glyph, Size = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _iconBox = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(12), Child = _icon };
            return _iconBox;
        }

        public void Show()
        {
            bool on = Button.IsChecked == true;
            _ring.Bind(Shape.StrokeProperty, _ring.GetResourceObservable(on ? "AccentBrush" : "Text3Brush"));
            _dot.IsVisible = on;
            if (_iconBox is not null && _icon is not null)
            {
                _iconBox.Bind(Border.BackgroundProperty, _iconBox.GetResourceObservable(on ? "InfoBgBrush" : "Card2Brush"));
                _icon.Bind(StrokeIcon.ForegroundProperty, _icon.GetResourceObservable(on ? "InfoBrush" : "Text2Brush"));
            }
        }
    }

    /// <summary>
    /// The big two-column card of steps 1 and 3: icon box and radio on top, title (+ "แนะนำ") and description, then
    /// <paramref name="extra"/> rows.
    /// </summary>
    protected static OptionCard BigCard(string group, string glyph, string title, bool recommended, string desc, params Control[] extra)
    {
        var card = new OptionCard(group);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        top.Children.Add(card.IconBox(glyph));
        Grid.SetColumn(card.Radio, 2);
        top.Children.Add(card.Radio);

        var head = new WrapPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Classes = { "lbl" }, FontSize = 18, Text = title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        if (recommended)
            head.Children.Add(new Border { Classes = { "pill", "info" }, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = Loc.Get("TranslateRecommended") } });
        var text = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                head,
                new TextBlock { Classes = { "mut" }, FontSize = 14, LineHeight = 22, Text = desc, TextWrapping = TextWrapping.Wrap },
            },
        };

        var stack = new StackPanel { Spacing = 14, Margin = new Thickness(4), Children = { top, text } };
        foreach (var control in extra)
            stack.Children.Add(control);
        card.Button.Content = stack;
        return card;
    }

    /// <summary>Two equal columns, 16 px apart.</summary>
    protected static Grid TwoColumns(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16 };
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>A bullet line of a card: tinted glyph and 14 px text.</summary>
    protected static Grid BulletLine(string glyph, string brush, string text)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        var icon = new StrokeIcon { Data = glyph, Size = 16, StrokeThickness = 2.2, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
        icon.Bind(StrokeIcon.ForegroundProperty, icon.GetResourceObservable(brush));
        row.Children.Add(icon);
        var block = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(block, 1);
        row.Children.Add(block);
        return row;
    }

    /// <summary>An info note (Border.note.info): glyph, then <paramref name="lead"/> followed by <paramref name="bold"/> in bold.</summary>
    protected static Border InfoNote(string glyph, string lead, string bold)
    {
        var text = new TextBlock { FontSize = 14, LineHeight = 22, TextWrapping = TextWrapping.Wrap };
        text.Inlines = new InlineCollection { new Run(lead), new Run(bold) { FontWeight = FontWeight.Bold } };
        var icon = new StrokeIcon { Data = glyph, Size = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        row.Children.Add(icon);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return new Border { Classes = { "note", "info" }, Padding = new Thickness(14, 12), Child = row };
    }

    /// <summary>A key cap (Border.kbd).</summary>
    protected static Border Kbd(string cap) => new() { Classes = { "kbd" }, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = cap } };
}
