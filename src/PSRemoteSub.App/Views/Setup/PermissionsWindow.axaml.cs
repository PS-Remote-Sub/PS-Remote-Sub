using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PSRemoteSub.App.Views.Controls;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.App.Views.Setup;

/// <summary>
/// macOS only (mockup New-Mac-Permissions): one row per permission with its state as macOS reports it ("อนุญาตแล้ว" only
/// when granted, otherwise "อนุญาต" which asks the OS or opens System Settings). Re-reads the state when the window
/// regains focus (that is when a grant made in System Settings shows up). "ทำทีหลัง" and "เสร็จแล้ว" just close it.
/// UI thread only.
/// </summary>
public partial class PermissionsWindow : Window
{
    private const string CheckGlyph = "M5 12.5l4.5 4.5L19 7.5";
    private const string InfoGlyph = "M12 21.5a9.5 9.5 0 1 0 0-19 9.5 9.5 0 0 0 0 19zM12 11v5.5M12 7.8h.01";
    private const string KeyboardGlyph = "M3 6h18v12H3zM7 10h.01M11 10h.01M15 10h.01M7 14h10";

    private sealed record Item(PlatformPermission Permission, string Glyph, bool Required, string Badge);

    private static readonly Item[] Items =
    [
        new(PlatformPermission.ScreenRecording, "M3 5h18v12H3zM8 21h8M12 17v4", true, "PermBadgeRequired"),
        new(PlatformPermission.Camera, "M4 7h3l2-2.5h6L17 7h3v12H4zM12 16.5a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7z", false, "PermBadgeCapture"),
        new(PlatformPermission.Microphone, "M12 3a3 3 0 0 1 3 3v5a3 3 0 0 1-6 0V6a3 3 0 0 1 3-3zM5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21", false, "PermBadgeCapture"),
        new(PlatformPermission.LocalNetwork, "M2.5 9a14 14 0 0 1 19 0M5.5 12.5a9.5 9.5 0 0 1 13 0M8.8 16a4.8 4.8 0 0 1 6.4 0M12 19.5h.01", false, "PermBadgePhoneTv"),
    ];

    private readonly AppHost? _host;
    private PermissionState[]? _shown; // what the rows show (null: not built yet)
    private bool _forExit;

    /// <summary>For the XAML loader and designer only.</summary>
    public PermissionsWindow() => InitializeComponent();

    public PermissionsWindow(AppHost host) : this()
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        LaterButton.Click += (_, _) => Close();
        DoneButton.Click += (_, _) => Close();
        Activated += (_, _) => Safe(() =>
        {
            host.RefreshPermissions();
            Rebuild();
        });
        host.StateChanged += OnHostStateChanged;
        ActualThemeVariantChanged += (_, _) => Safe(() =>
        {
            _shown = null; // brushes are resolved per theme
            Rebuild();
        });
        Closed += (_, _) => host.StateChanged -= OnHostStateChanged;
        BuildNotes();
        Rebuild();
    }

    /// <summary>Closes for app exit (the first-run continuation must not start the wizard then).</summary>
    internal void CloseForExit()
    {
        _forExit = true;
        Close();
    }

    /// <summary>True when the window was closed because the app is exiting.</summary>
    internal bool ClosedForExit => _forExit;

    private AppHost Host => _host ?? throw new InvalidOperationException("No host");

    private void OnHostStateChanged(object? sender, EventArgs e) => Safe(Rebuild);

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn("Permissions window failed", ex);
        }
    }

    /// <summary>Rebuilds the rows when any state changed (StateChanged fires often; a rebuild under the pointer can swallow a click).</summary>
    private void Rebuild()
    {
        var states = Items.Select(i => Host.PermissionStateOf(i.Permission)).ToArray();
        if (_shown is { } shown && shown.SequenceEqual(states))
            return;
        _shown = states;
        Rows.Children.Clear();
        for (int i = 0; i < Items.Length; i++)
        {
            if (i > 0)
                Rows.Children.Add(new Border { Classes = { "hr" } });
            Rows.Children.Add(BuildRow(Items[i], states[i]));
        }
    }

    private Control BuildRow(Item item, PermissionState state)
    {
        bool granted = state == PermissionState.Granted;
        bool unknown = state == PermissionState.Unknown;
        string key = item.Permission.ToString();
        var tile = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(12),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brush(granted ? "OkBgBrush" : "InfoBgBrush"),
            Child = new StrokeIcon
            {
                Data = item.Glyph,
                Size = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush(granted ? "OkBrush" : "InfoBrush"),
            },
        };
        var badge = new Border
        {
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = item.Required ? Brush("InfoBgBrush") : Brush("Card2Brush"),
            Child = new TextBlock
            {
                Text = Loc.Get(item.Badge),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush(item.Required ? "InfoBrush" : "Text2Brush"),
            },
        };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock { Text = Loc.Get($"Perm{key}Name"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Classes = { "sub" }, Text = $"({AppHost.PermissionName(item.Permission)})", VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(badge);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(head);
        text.Children.Add(new TextBlock { Classes = { "mut" }, FontSize = 14, Text = Loc.Get($"Perm{key}Why"), TextWrapping = TextWrapping.Wrap });

        if (unknown && item.Permission == PlatformPermission.LocalNetwork)
            text.Children.Add(new TextBlock { Classes = { "sub" }, Text = Loc.Get("PermLocalNetworkAsks"), TextWrapping = TextWrapping.Wrap });

        Control right;
        if (unknown)
        {
            // The OS cannot say (Local Network always): no "อนุญาต" button that would promise something, never the granted pill.
            var settings = new Button { Content = Loc.Get("PermOpenSettingsShort"), Classes = { "btn", "ghost", "sm" }, VerticalAlignment = VerticalAlignment.Center };
            var permission = item.Permission;
            settings.Click += (_, _) => Safe(() => Host.OpenPermissionSettings(permission));
            right = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new Border { Classes = { "pill", "info" }, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = Loc.Get("PermUnknown") } },
                    settings,
                },
            };
        }
        else if (granted)
        {
            right = new Border
            {
                Classes = { "pill", "ok" },
                VerticalAlignment = VerticalAlignment.Center,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new StrokeIcon { Data = CheckGlyph, Size = 14, StrokeThickness = 2.4, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = Loc.Get("PermGranted") },
                    },
                },
            };
        }
        else
        {
            var allow = new Button { Content = Loc.Get("PermAllow"), Classes = { "btn", "primary", "sm" }, VerticalAlignment = VerticalAlignment.Center };
            var permission = item.Permission;
            allow.Click += (_, _) => Safe(() => Host.RequestPermission(permission));
            right = allow;
        }

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 16, Margin = new Thickness(0, 16) };
        row.Children.Add(tile);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Grid.SetColumn(right, 2);
        row.Children.Add(right);
        return row;
    }

    private void BuildNotes()
    {
        var note = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        note.Children.Add(new StrokeIcon { Data = InfoGlyph, Size = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var noteText = new TextBlock { Text = Loc.Get("PermReaskNote"), TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        Grid.SetColumn(noteText, 1);
        note.Children.Add(noteText);
        Notes.Children.Add(new Border { Classes = { "note", "info" }, Child = note });

        var caps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var cap in new[] { "⌃", "⌥", "⇧" })
            caps.Children.Add(new Border { Classes = { "kbd" }, Child = new TextBlock { Text = cap } });
        var shortcut = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        shortcut.Children.Add(new StrokeIcon { Data = KeyboardGlyph, Size = 18, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("Text2Brush") });
        var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        wrap.Children.Add(Small(Loc.Get("PermShortcutBefore")));
        wrap.Children.Add(caps);
        wrap.Children.Add(Small(Loc.Get("PermShortcutAfter")));
        Grid.SetColumn(wrap, 1);
        shortcut.Children.Add(wrap);
        Notes.Children.Add(shortcut);
    }

    private static TextBlock Small(string text) => new()
    {
        Text = text,
        FontSize = 14,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
    };

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
}
