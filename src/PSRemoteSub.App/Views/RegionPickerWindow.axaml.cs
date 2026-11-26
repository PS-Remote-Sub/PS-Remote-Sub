using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Layout;
using PixelRect = PSRemoteSub.Core.Imaging.PixelRect;

namespace PSRemoteSub.App.Views;

/// <summary>One zone as the box editor opens it: its boxes as fractions of the client area, and which of them it shows.</summary>
/// <param name="ShowCaption">The caption box is shown and editable (the Custom placement).</param>
/// <param name="ShowTrigger">The trigger box is shown and editable (the trigger is on).</param>
public sealed record ZoneBoxes(string Id, string Name, RegionFraction? Ocr, RegionFraction? Caption,
    RegionFraction? Trigger, bool ShowCaption, bool ShowTrigger);

/// <summary>A zone whose boxes the user changed: all three boxes as they are now.</summary>
/// <param name="CaptionChanged">The user moved, resized or redrew the caption box in this edit.</param>
public sealed record ZoneBoxEdit(string Id, RegionFraction? Ocr, RegionFraction? Caption, RegionFraction? Trigger,
    bool CaptionChanged);

/// <summary>
/// Borderless topmost window laid exactly over the Remote Play client area, showing a frozen frame dimmed to 50 %
/// outside the selected zone's boxes: read/OCR (blue), translation/caption (orange, Custom placement only) and trigger
/// (purple, trigger on only). The other zones' boxes show faint with their names; a click on one selects that zone. The
/// toolbar on top (New-RegionPicker) selects the zone (chips, or keys 1–9), adds a zone, switches the box being edited
/// (or Tab), and cancels/saves. Each box moves by dragging inside it and resizes from its corners and edges; dragging on
/// an empty area (or anywhere, while the active box does not exist yet) draws the active box. Enter saves every zone
/// changed, Esc or a right-click cancels everything. Boxes are kept as fractions of the client area, so a DPI rescale
/// while open changes nothing. UI thread only.
/// </summary>
public partial class RegionPickerWindow : Window
{
    /// <summary>Smallest box accepted, in DIP.</summary>
    private const double MinSelectionWidth = 20;
    private const double MinSelectionHeight = 10;

    /// <summary>How close to a corner or edge a press grabs it, in DIP.</summary>
    private const double Grip = 8;

    /// <summary>The bold words of the hint row (the dark theme's text colour: this window is always dark).</summary>
    private static readonly IImmutableSolidColorBrush HintStrongBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));

    private static RegionPickerWindow? s_open;

    private readonly TaskCompletionSource<(IReadOnlyList<ZoneBoxEdit> Edits, string LastZoneId)?> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WriteableBitmap? _bitmap;
    private PixelRect _client;
    private (IReadOnlyList<ZoneBoxEdit> Edits, string LastZoneId)? _picked;

    private List<ZoneState> _zones = [];
    private ZoneState _zone = null!; // the selected zone, set by Load
    private readonly List<RadioButton> _chips = [];
    private Func<ZoneBoxes>? _newZone; // makes a zone for "add zone"; null hides the button

    // The drag in progress, always on the selected zone (zones cannot change while dragging): which box, what was
    // grabbed (None = a free drag that draws the box), where it started and the box before it (restored when a free
    // drag is too small).
    private bool _dragging;
    private bool _moved; // the pointer has gone past the drag threshold since the press
    private EditorBox _dragBox;
    private BoxHandle _dragHandle;
    private Point _dragStart;
    private EditRect _dragStartRect;
    private RegionFraction? _beforeDrag;
    private EditorBox? _clickSelects; // what a click (no drag) selects when the press would draw a missing box
    private ZoneState? _clickZone; // the other zone a click (no drag) selects, when the press hit only its box

    public RegionPickerWindow()
    {
        InitializeComponent();
        // Ruling 3: every handler catches all exceptions. A failing input handler closes the picker (it returns
        // null) so the caller's re-entrancy guard and overlay suppression are always released.
        Surface.PointerPressed += (_, e) => Guard(() => OnSurfacePressed(e), closeOnError: true);
        Surface.PointerMoved += (_, e) => Guard(() => OnSurfaceMoved(e), closeOnError: true);
        Surface.PointerReleased += (_, e) => Guard(() => OnSurfaceReleased(e), closeOnError: true);
        Surface.PointerCaptureLost += (_, _) => Guard(FinishDrag, closeOnError: true);
        // Tunnel: the keys are handled before anything else sees them, so Tab never moves focus.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddZoneButton.Click += (_, _) => Guard(AddZone, closeOnError: true);
        OcrSwitch.Click += (_, _) => Guard(() => SwitchBox(EditorBox.Ocr), closeOnError: true);
        CaptionSwitch.Click += (_, _) => Guard(() => SwitchBox(EditorBox.Caption), closeOnError: true);
        TriggerSwitch.Click += (_, _) => Guard(() => SwitchBox(EditorBox.Trigger), closeOnError: true);
        CancelButton.Click += (_, _) => Guard(Close, closeOnError: true);
        SaveButton.Click += (_, _) => Guard(Save, closeOnError: true);
        HideKeysButton.Click += (_, _) => KeyLegend.IsVisible = false;
        BuildHint();
        Opened += (_, _) => Guard(() =>
        {
            // If the window opened on a monitor with another DPI than the one under the client area, Windows has
            // rescaled it by now: size it again with the scaling it actually has.
            if (DesktopScaling > 0 && _client.Width > 0)
            {
                Position = new PixelPoint(_client.X, _client.Y);
                Width = _client.Width / DesktopScaling;
                Height = _client.Height / DesktopScaling;
            }
            Activate();
            Focus();
        }, closeOnError: false);
        Closed += (_, _) =>
        {
            // Complete first: nothing below may keep the caller waiting.
            _result.TrySetResult(_picked);
            if (s_open == this)
                s_open = null;
            try
            {
                Surface.Bitmap = null;
                _bitmap?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("RegionPicker: releasing the frozen frame failed", ex);
            }
            _bitmap = null;
        };
    }

    /// <summary>
    /// UI thread. Shows <paramref name="frozen"/> over <paramref name="clientScreen"/> (physical pixels) with the boxes of
    /// <paramref name="zones"/>, starting on <paramref name="startZoneId"/> (the first zone when unknown), and lets the
    /// user move, resize and draw them. Returns every zone whose boxes changed and the zone selected last, or null on
    /// Esc, right-click or close. Only one editor is open at a time; a second call while one is open returns null.
    /// "Add zone" calls <paramref name="newZone"/> (hidden when null) for each zone added: the caller keeps those zones
    /// aside and adds them only when the editor returns a result (a cancel adds nothing).
    /// </summary>
    public static Task<(IReadOnlyList<ZoneBoxEdit> Edits, string LastZoneId)?> PickAsync(Frame frozen,
        PixelRect clientScreen, IReadOnlyList<ZoneBoxes> zones, string startZoneId, Func<ZoneBoxes>? newZone = null)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        ArgumentNullException.ThrowIfNull(zones);
        if (s_open is not null || zones.Count == 0 || frozen.Width <= 0 || frozen.Height <= 0
            || clientScreen.Width <= 0 || clientScreen.Height <= 0)
            return Task.FromResult<(IReadOnlyList<ZoneBoxEdit> Edits, string LastZoneId)?>(null);

        var window = new RegionPickerWindow();
        try
        {
            window._newZone = newZone;
            window.AddZoneButton.IsVisible = newZone is not null;
            window.Load(frozen, clientScreen, zones, startZoneId);
            s_open = window;
            window.Show();
        }
        catch (Exception)
        {
            s_open = null;
            window._bitmap?.Dispose();
            throw;
        }
        return window._result.Task;
    }

    /// <summary>UI thread. Closes an open editor (it returns null), for example on exit.</summary>
    public static void CancelOpen() => s_open?.Close();

    private void Load(Frame frozen, PixelRect clientScreen, IReadOnlyList<ZoneBoxes> zones, string startZoneId)
    {
        _bitmap = ToBitmap(frozen);
        Surface.Bitmap = _bitmap;
        _client = clientScreen;
        _zones = zones.Select(z => new ZoneState(z)).ToList();
        _zone = _zones.FirstOrDefault(z => z.Source.Id == startZoneId) ?? _zones[0];
        BuildChips();
        ShowBoxes();

        // Same conversion as OverlayWindow: position in physical pixels, size in DIP of the screen under the
        // centre of the client area. Boxes are divided by the window's DIP size, so DPI cancels out.
        var center = new PixelPoint(clientScreen.X + clientScreen.Width / 2, clientScreen.Y + clientScreen.Height / 2);
        var screen = Screens.ScreenFromPoint(center) ?? Screens.Primary;
        double scaling = screen?.Scaling ?? 1;
        Position = new PixelPoint(clientScreen.X, clientScreen.Y);
        Width = clientScreen.Width / scaling;
        Height = clientScreen.Height / scaling;
    }

    private static WriteableBitmap ToBitmap(Frame frame)
    {
        var bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var buffer = bitmap.Lock();
        int rowBytes = frame.Width * 4;
        for (int row = 0; row < frame.Height; row++)
            Marshal.Copy(frame.Bgra, row * rowBytes, buffer.Address + row * buffer.RowBytes, rowBytes);
        return bitmap;
    }

    /// <summary>
    /// One segment per zone ("1 บทพูด"; no number past 9, as only 1–9 are keys); never focusable, so Enter, Tab and the
    /// digits always reach the window. New controls each time (a control is never re-parented).
    /// </summary>
    private void BuildChips()
    {
        ZoneChips.Children.Clear();
        _chips.Clear();
        for (int i = 0; i < _zones.Count; i++)
        {
            var zone = _zones[i];
            var chip = new RadioButton
            {
                Content = ZoneLabel(i),
                Focusable = false,
                IsTabStop = false,
                GroupName = "PickerZones",
            };
            chip.Classes.Add("seg");
            chip.Click += (_, _) => Guard(() => SelectZone(zone), closeOnError: true);
            _chips.Add(chip);
            ZoneChips.Children.Add(chip);
        }
    }

    /// <summary>"N name" for zones 1–9 (their key), the name alone after that.</summary>
    private string ZoneLabel(int index) =>
        index < 9 ? $"{index + 1} {_zones[index].Source.Name}" : _zones[index].Source.Name;

    private void BuildHint()
    {
        var inlines = new InlineCollection();
        void Part(string strong, string rest, bool last)
        {
            inlines.Add(new Run(Loc.Get(strong)) { FontWeight = FontWeight.SemiBold, Foreground = HintStrongBrush });
            inlines.Add(new Run(" " + Loc.Get(rest) + (last ? "" : " · ")));
        }
        Part("PickerHintDraw", "PickerHintDrawRest", last: false);
        Part("PickerHintMove", "PickerHintMoveRest", last: false);
        Part("PickerHintResize", "PickerHintResizeRest", last: true);
        HintText.Inlines = inlines;
    }

    private void SelectZone(ZoneState zone)
    {
        if (!_dragging && zone != _zone)
            _zone = zone;
        ShowBoxes(); // also puts a clicked chip back when the zone did not change
    }

    /// <summary>"Add zone": a new zone from the caller, selected, with nothing drawn yet (a drag draws its read box).</summary>
    private void AddZone()
    {
        if (_dragging || _newZone is null)
            return;
        var zone = new ZoneState(_newZone());
        _zones.Add(zone);
        BuildChips();
        _zone = zone;
        ShowBoxes();
    }

    /// <summary>The box switch: edits <paramref name="box"/> of the selected zone if it shows it and has a read box.</summary>
    private void SwitchBox(EditorBox box)
    {
        if (!_dragging && _zone.Shown.Contains(box) && (_zone.Ocr is not null || box == EditorBox.Ocr))
            _zone.Active = box;
        ShowBoxes(); // also puts the switch back when nothing changed
    }

    private void Save()
    {
        FinishDrag(); // Enter mid-drag: the same checks as a release
        _picked = (_zones.Select(z => z.Edit()).OfType<ZoneBoxEdit>().ToList(), _zone.Source.Id);
        Close();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            switch (e.Key)
            {
                case Key.Escape:
                    e.Handled = true;
                    Close();
                    return;
                case Key.Enter:
                    e.Handled = true;
                    Save();
                    return;
                case Key.Tab:
                    // Never move focus away. Read -> translation (-> trigger, when on) -> read; a zone that shows only
                    // its read box stays on it.
                    e.Handled = true;
                    if (!_dragging && _zone.Ocr is not null)
                    {
                        var shown = _zone.Shown;
                        _zone.Active = shown[(shown.IndexOf(_zone.Active) + 1) % shown.Count];
                        ShowBoxes();
                    }
                    return;
                case >= Key.D1 and <= Key.D9:
                case >= Key.NumPad1 and <= Key.NumPad9:
                    e.Handled = true;
                    int index = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 : e.Key - Key.D1;
                    if (index < _zones.Count) // no such zone: nothing happens
                        SelectZone(_zones[index]);
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("RegionPicker: key handler failed", ex);
            SafeClose();
        }
    }

    /// <summary>Runs a handler; on failure traces it and, if asked, closes the editor (result null).</summary>
    private void Guard(Action action, bool closeOnError)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn("RegionPicker: handler failed", ex);
            if (closeOnError)
                SafeClose();
        }
    }

    private void SafeClose()
    {
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            Log.Warn("RegionPicker: close failed", ex);
            // Closed may never run: release the caller anyway.
            _result.TrySetResult(null);
            if (s_open == this)
                s_open = null;
        }
    }

    private Size SurfaceSize => Surface.Bounds.Size;

    private EditRect? ToDip(RegionFraction? f)
    {
        var size = SurfaceSize;
        return f is null ? null : new EditRect(f.X * size.Width, f.Y * size.Height, f.Width * size.Width, f.Height * size.Height);
    }

    private RegionFraction ToFraction(EditRect r)
    {
        var size = SurfaceSize;
        return new RegionFraction(r.X / size.Width, r.Y / size.Height, r.Width / size.Width, r.Height / size.Height);
    }

    /// <summary>The selected zone's shown boxes in DIP, the only ones a press can grab.</summary>
    private List<(EditorBox Box, EditRect? Rect)> Grabbable() =>
        _zone.Shown.Select(b => (b, ToDip(_zone.Get(b)))).ToList();

    private void ShowBoxes()
    {
        // Other zones: every shown box faint; the label once per zone, on its first box.
        var others = new List<(string? Label, RegionFraction Region)>();
        for (int i = 0; i < _zones.Count; i++)
        {
            var zone = _zones[i];
            if (zone == _zone)
                continue;
            string? label = Loc.Format("PickerOtherZone", ZoneLabel(i));
            foreach (var box in zone.Shown)
                if (zone.Get(box) is { } region)
                {
                    others.Add((label, region));
                    label = null;
                }
        }
        Surface.Show(_zone.Shown.Select(b => (b, _zone.Get(b))).ToList(), _zone.Active, others);
        for (int i = 0; i < _chips.Count; i++)
            _chips[i].IsChecked = _zones[i] == _zone;
        bool hasOcr = _zone.Ocr is not null;
        OcrSwitch.IsChecked = _zone.Active == EditorBox.Ocr;
        CaptionSwitch.IsChecked = _zone.Active == EditorBox.Caption;
        TriggerSwitch.IsChecked = _zone.Active == EditorBox.Trigger;
        CaptionSwitch.IsVisible = _zone.Shown.Contains(EditorBox.Caption);
        TriggerSwitch.IsVisible = _zone.Shown.Contains(EditorBox.Trigger);
        CaptionSwitch.IsEnabled = TriggerSwitch.IsEnabled = hasOcr; // the read box comes first
    }

    /// <summary>The other zone whose box is under <paramref name="p"/> (DIP), the last drawn first; null when none.</summary>
    private ZoneState? OtherZoneAt(Point p)
    {
        foreach (var zone in Enumerable.Reverse(_zones))
        {
            if (zone == _zone)
                continue;
            foreach (var box in zone.Shown)
                if (ToDip(zone.Get(box)) is { } r && p.X >= r.X && p.X <= r.X + r.Width && p.Y >= r.Y && p.Y <= r.Y + r.Height)
                    return zone;
        }
        return null;
    }

    private void OnSurfacePressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(Surface);
        if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            Close();
            return;
        }
        if (!point.Properties.IsLeftButtonPressed || SurfaceSize.Width <= 0 || SurfaceSize.Height <= 0)
            return;
        e.Handled = true;
        var p = Clamp(point.Position);
        var (box, handle) = BoxEditor.Pick(Grabbable(), _zone.Active, p.X, p.Y, Grip);
        _clickSelects = null;
        // Nothing of the selected zone under the press: a click on another zone's box selects that zone (a drag still
        // draws).
        _clickZone = handle == BoxHandle.None ? OtherZoneAt(p) : null;
        // With no OCR box yet, the only thing to do is draw one.
        if (_zone.Ocr is null)
        {
            (box, handle) = (EditorBox.Ocr, BoxHandle.None);
        }
        else if (_zone.Get(_zone.Active) is null)
        {
            // The active box does not exist yet: a drag draws it, even over another box; a click selects what it hit.
            if (handle != BoxHandle.None)
                _clickSelects = box;
            (box, handle) = (_zone.Active, BoxHandle.None);
        }
        _zone.Active = box;
        _dragBox = box;
        _dragHandle = handle;
        _dragStart = p;
        _beforeDrag = _zone.Get(box);
        _dragStartRect = ToDip(_beforeDrag) ?? default;
        _dragging = true;
        _moved = false;
        e.Pointer.Capture(Surface);
        ShowBoxes();
    }

    private void OnSurfaceMoved(PointerEventArgs e)
    {
        var p = Clamp(e.GetPosition(Surface));
        if (!_dragging)
        {
            var (_, hover) = BoxEditor.Pick(Grabbable(), _zone.Active, p.X, p.Y, Grip);
            bool draws = _zone.Ocr is null || _zone.Get(_zone.Active) is null;
            if (draws)
                hover = BoxHandle.None;
            Surface.Cursor = hover == BoxHandle.None && OtherZoneAt(p) is not null ? HandCursor : CursorFor(hover);
            return;
        }
        ApplyDrag(p);
    }

    /// <summary>Updates the dragged box for the pointer at <paramref name="p"/>; a click's jitter changes nothing.</summary>
    private void ApplyDrag(Point p)
    {
        if (!_moved && !BoxEditor.IsDrag(p.X - _dragStart.X, p.Y - _dragStart.Y))
            return;
        _moved = true;
        var size = SurfaceSize;
        var rect = _dragHandle == BoxHandle.None
            ? BoxEditor.FromDrag(_dragStart.X, _dragStart.Y, p.X, p.Y, size.Width, size.Height)
            : BoxEditor.Drag(_dragStartRect, _dragHandle, p.X - _dragStart.X, p.Y - _dragStart.Y,
                size.Width, size.Height, MinSelectionWidth, MinSelectionHeight);
        _zone.Set(_dragBox, rect.Width > 0 && rect.Height > 0 ? ToFraction(rect) : _beforeDrag);
        ShowBoxes();
    }

    private void OnSurfaceReleased(PointerReleasedEventArgs e)
    {
        if (!_dragging || e.InitialPressMouseButton != MouseButton.Left)
            return;
        e.Handled = true;
        ApplyDrag(Clamp(e.GetPosition(Surface)));
        FinishDrag(); // before releasing capture, which raises PointerCaptureLost
        e.Pointer.Capture(null);
    }

    /// <summary>
    /// Ends the drag in progress, however it ends (release, lost capture, Enter): a free drag smaller than the minimum
    /// (or a click) keeps the box it had; the first OCR box gets a caption box copied from it (when the zone shows one);
    /// a caption box that really moved is marked changed.
    /// </summary>
    private void FinishDrag()
    {
        if (!_dragging)
            return;
        _dragging = false;
        if (!_moved)
        {
            // A click: it only selects a box, or another zone.
            if (_clickSelects is { } selected)
            {
                _zone.Active = selected;
                ShowBoxes();
            }
            else if (_clickZone is { } other)
            {
                SelectZone(other);
            }
            _clickZone = null;
            return;
        }
        _clickZone = null;
        if (_dragHandle == BoxHandle.None)
        {
            var rect = ToDip(_zone.Get(_dragBox));
            if (rect is not { Width: >= MinSelectionWidth, Height: >= MinSelectionHeight })
            {
                _zone.Set(_dragBox, _beforeDrag); // too small: keep the box it had
                ShowBoxes();
                return;
            }
            if (_dragBox == EditorBox.Ocr && _zone.Caption is null && _zone.Source.ShowCaption)
            {
                // First OCR box: the caption box starts as a copy of it, and is what the user adjusts next.
                _zone.Caption = _zone.Ocr;
                _zone.Active = EditorBox.Caption;
            }
        }
        if (_dragBox == EditorBox.Caption && _zone.Caption != _beforeDrag)
            _zone.CaptionChanged = true;
        ShowBoxes();
    }

    private Point Clamp(Point p) => new(
        Math.Clamp(p.X, 0, Surface.Bounds.Width),
        Math.Clamp(p.Y, 0, Surface.Bounds.Height));

    private static readonly Dictionary<BoxHandle, Cursor> Cursors = new()
    {
        [BoxHandle.None] = new Cursor(StandardCursorType.Cross),
        [BoxHandle.Move] = new Cursor(StandardCursorType.SizeAll),
        [BoxHandle.N] = new Cursor(StandardCursorType.TopSide),
        [BoxHandle.S] = new Cursor(StandardCursorType.BottomSide),
        [BoxHandle.W] = new Cursor(StandardCursorType.LeftSide),
        [BoxHandle.E] = new Cursor(StandardCursorType.RightSide),
        [BoxHandle.NW] = new Cursor(StandardCursorType.TopLeftCorner),
        [BoxHandle.NE] = new Cursor(StandardCursorType.TopRightCorner),
        [BoxHandle.SW] = new Cursor(StandardCursorType.BottomLeftCorner),
        [BoxHandle.SE] = new Cursor(StandardCursorType.BottomRightCorner),
    };

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private static Cursor CursorFor(BoxHandle handle) => Cursors[handle];

    /// <summary>One zone's boxes while the editor is open, and what it started with (to tell whether it changed).</summary>
    private sealed class ZoneState
    {
        private readonly RegionFraction? _initialOcr, _initialCaption, _initialTrigger;

        public ZoneState(ZoneBoxes source)
        {
            Source = source;
            Ocr = source.Ocr;
            // A shown caption box starts as the OCR box when unset, as it is drawn there.
            Caption = source.ShowCaption && source.Ocr is not null ? source.Caption ?? source.Ocr : source.Caption;
            Trigger = source.Trigger;
            (_initialOcr, _initialCaption, _initialTrigger) = (Ocr, Caption, Trigger);
            Active = Ocr is null || !source.ShowCaption ? EditorBox.Ocr : EditorBox.Caption;
            Shown = [EditorBox.Ocr];
            if (source.ShowCaption)
                Shown.Add(EditorBox.Caption);
            if (source.ShowTrigger)
                Shown.Add(EditorBox.Trigger);
        }

        public ZoneBoxes Source { get; }
        public RegionFraction? Ocr { get; set; }
        public RegionFraction? Caption { get; set; }
        public RegionFraction? Trigger { get; set; }
        public bool CaptionChanged { get; set; }
        public EditorBox Active { get; set; }

        /// <summary>The boxes this zone shows, in Tab order: OCR, then caption, then trigger.</summary>
        public List<EditorBox> Shown { get; }

        public RegionFraction? Get(EditorBox box) => box switch
        {
            EditorBox.Ocr => Ocr,
            EditorBox.Caption => Caption,
            _ => Trigger,
        };

        public void Set(EditorBox box, RegionFraction? value)
        {
            switch (box)
            {
                case EditorBox.Ocr: Ocr = value; break;
                case EditorBox.Caption: Caption = value; break;
                default: Trigger = value; break;
            }
        }

        /// <summary>The edit to save, or null when no box of this zone changed.</summary>
        public ZoneBoxEdit? Edit() =>
            CaptionChanged || Ocr != _initialOcr || Caption != _initialCaption || Trigger != _initialTrigger
                ? new ZoneBoxEdit(Source.Id, Ocr, Caption, Trigger, CaptionChanged)
                : null;
    }
}

/// <summary>
/// Draws the frozen frame stretched to its bounds, dimmed to 50 % outside the selected zone's boxes, with the read box
/// solid blue, the translation box dashed orange and the trigger box dotted purple (New-RegionPicker), each tagged with
/// what it is ("อ่านซับตรงนี้"…), the active one tinted with round corner handles; and the other zones' boxes faint
/// white, tagged once per zone with its name.
/// </summary>
public sealed class RegionSelectionCanvas : Control
{
    public static readonly Color OcrColor = Color.FromRgb(0x5A, 0xA8, 0xFF);
    public static readonly Color CaptionColor = Color.FromRgb(0xFF, 0x9D, 0x4D);
    public static readonly Color TriggerColor = Color.FromRgb(0xC5, 0x8B, 0xFF);

    /// <summary>Diameter of a corner handle, in DIP.</summary>
    private const double HandleSize = 12;

    private const double BoxThickness = 3;

    private static readonly Typeface LabelFace = new(
        new FontFamily("avares://PSRemoteSub.App/Assets/Fonts#IBM Plex Sans Thai"), FontStyle.Normal, FontWeight.SemiBold);

    /// <summary>Text on a coloured tag (the mockup's on-accent colour).</summary>
    private static readonly IImmutableSolidColorBrush TagText = new ImmutableSolidColorBrush(Color.FromRgb(0x08, 0x11, 0x1F));

    private static readonly IImmutableSolidColorBrush Dim = new ImmutableSolidColorBrush(Color.FromArgb(128, 0, 0, 0));
    private static readonly IImmutableSolidColorBrush OcrBrush = new ImmutableSolidColorBrush(OcrColor);
    private static readonly IImmutableSolidColorBrush CaptionBrush = new ImmutableSolidColorBrush(CaptionColor);
    private static readonly IImmutableSolidColorBrush TriggerBrush = new ImmutableSolidColorBrush(TriggerColor);
    private static readonly IImmutableSolidColorBrush HandleFill = new ImmutableSolidColorBrush(Colors.White);
    private static readonly ImmutablePen OtherPen = new(new ImmutableSolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), 2,
        new ImmutableDashStyle([3, 2], 0));
    private static readonly IImmutableSolidColorBrush OtherTagBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xCC, 0x14, 0x16, 0x1B));
    private static readonly IImmutableSolidColorBrush OtherTagText = new ImmutableSolidColorBrush(Color.FromRgb(0xC9, 0xCE, 0xD6));

    private Bitmap? _bitmap;
    private IReadOnlyList<(EditorBox Box, RegionFraction? Region)> _boxes = [];
    private EditorBox _active;
    private IReadOnlyList<(string? Label, RegionFraction Region)> _others = [];

    public RegionSelectionCanvas()
    {
        Focusable = false;
        ClipToBounds = true;
    }

    public Bitmap? Bitmap
    {
        get => _bitmap;
        set { _bitmap = value; InvalidateVisual(); }
    }

    /// <summary>
    /// Shows the selected zone's <paramref name="boxes"/> (fractions of the bounds; null ones are not drawn) with
    /// <paramref name="active"/> on top, and the other zones' boxes faint (a null label draws no tag).
    /// </summary>
    public void Show(IReadOnlyList<(EditorBox Box, RegionFraction? Region)> boxes, EditorBox active,
        IReadOnlyList<(string? Label, RegionFraction Region)> others)
    {
        _boxes = boxes;
        _active = active;
        _others = others;
        InvalidateVisual();
    }

    private Rect? ToRect(RegionFraction? f) =>
        f is null ? null : new Rect(f.X * Bounds.Width, f.Y * Bounds.Height, f.Width * Bounds.Width, f.Height * Bounds.Height);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        // A transparent fill keeps the whole surface hit-testable.
        context.FillRectangle(Brushes.Transparent, bounds);
        var boxes = _boxes.Select(b => (b.Box, Rect: ToRect(b.Region))).ToList();
        if (_bitmap is { } bitmap)
        {
            context.DrawImage(bitmap, new Rect(bitmap.Size), bounds);
            context.FillRectangle(Dim, bounds);
            // Undimmed inside each box of the selected zone: redraw that part of the frame over the dim.
            foreach (var (_, r) in boxes)
                if (r is { Width: > 0, Height: > 0 } box)
                    context.DrawImage(bitmap, SourceOf(box, bitmap.Size, bounds.Size), box);
        }
        else
        {
            context.FillRectangle(Dim, bounds);
        }

        foreach (var (label, region) in _others)
            if (ToRect(region) is { Width: > 0, Height: > 0 } r)
            {
                context.DrawRectangle(null, OtherPen, r, 6, 6);
                if (label is not null)
                    DrawLabel(context, label, OtherTagBrush, r, OtherTagText);
            }

        // The active box last, so its outline and handles are on top.
        foreach (var (box, rect) in boxes.OrderBy(b => b.Box == _active))
            if (rect is { Width: > 0, Height: > 0 } r)
                DrawBox(context, box, r, box == _active);
    }

    private static Rect SourceOf(Rect dest, Size bitmapSize, Size bounds) => new(
        dest.X * bitmapSize.Width / bounds.Width, dest.Y * bitmapSize.Height / bounds.Height,
        dest.Width * bitmapSize.Width / bounds.Width, dest.Height * bitmapSize.Height / bounds.Height);

    private static IImmutableSolidColorBrush BrushOf(EditorBox box) => box switch
    {
        EditorBox.Ocr => OcrBrush,
        EditorBox.Caption => CaptionBrush,
        _ => TriggerBrush,
    };

    /// <summary>Solid read box, dashed translation box, dotted trigger box (as the toolbar's switch shows them).</summary>
    private static ImmutableDashStyle? DashOf(EditorBox box) => box switch
    {
        EditorBox.Ocr => null,
        EditorBox.Caption => new ImmutableDashStyle([2, 1.3], 0),
        _ => new ImmutableDashStyle([0.7, 1], 0),
    };

    private static string TagOf(EditorBox box) => Loc.Get(box switch
    {
        EditorBox.Ocr => "BoxTagOcr",
        EditorBox.Caption => "BoxTagCaption",
        _ => "BoxTagTrigger",
    });

    private void DrawBox(DrawingContext context, EditorBox box, Rect r, bool active)
    {
        var brush = BrushOf(box);
        var color = brush.Color;
        // Tinted inside: 10 % for the box being edited, 6 % for the others.
        context.FillRectangle(new ImmutableSolidColorBrush(Color.FromArgb(active ? (byte)26 : (byte)15, color.R, color.G, color.B)), r, 6);
        context.DrawRectangle(null, new ImmutablePen(brush, BoxThickness, DashOf(box)), r, 6, 6);
        // The translation tag sits under the box's right part, so it never covers a read box on top of it.
        DrawLabel(context, TagOf(box), brush, r, TagText, box == EditorBox.Caption ? Bounds.Height : null);
        if (!active)
            return;
        var outline = new ImmutablePen(brush, BoxThickness);
        foreach (var corner in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight })
            context.DrawEllipse(HandleFill, outline, corner, HandleSize / 2, HandleSize / 2);
    }

    /// <summary>
    /// A small coloured tag on the box: above its top-left (inside when the box touches the top), or, given
    /// <paramref name="belowWithin"/> (the surface height), under its bottom-right (inside when there is no room under it).
    /// </summary>
    internal static void DrawLabel(DrawingContext context, string text, IBrush background, Rect box,
        IBrush? foreground = null, double? belowWithin = null)
    {
        const double gap = 4;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            LabelFace, 14, foreground ?? TagText);
        var size = new Size(formatted.Width + 16, formatted.Height + 4);
        double x = box.Left, y;
        if (belowWithin is { } height)
        {
            x = Math.Max(box.Left, box.Right - size.Width - 40);
            y = box.Bottom + gap + size.Height <= height ? box.Bottom + gap : box.Bottom - size.Height - gap;
        }
        else
        {
            y = box.Top - size.Height - gap >= 0 ? box.Top - size.Height - gap : box.Top + gap;
        }
        var tag = new Rect(x, y, size.Width, size.Height);
        context.FillRectangle(background, tag, 6);
        context.DrawText(formatted, new Point(tag.X + 8, tag.Y + 2));
    }
}
