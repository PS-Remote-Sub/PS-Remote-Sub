namespace PSRemoteSub.Core.Layout;

/// <summary>A box being edited, in DIP of the editor surface.</summary>
public readonly record struct EditRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>What a press grabs: nothing, the body (move) or a corner or edge (resize).</summary>
public enum BoxHandle { None, Move, N, S, E, W, NE, NW, SE, SW }

/// <summary>The boxes of a zone the editor shows.</summary>
public enum EditorBox { Ocr, Caption, Trigger }

/// <summary>Pure geometry of the box editor: hit-testing, moving and resizing inside the surface.</summary>
public static class BoxEditor
{
    /// <summary>
    /// A corner within <paramref name="grip"/> of the point, else an edge within it (between the corners), else
    /// <see cref="BoxHandle.Move"/> strictly inside, else <see cref="BoxHandle.None"/>.
    /// </summary>
    public static BoxHandle HitHandle(EditRect box, double x, double y, double grip)
    {
        bool left = Math.Abs(x - box.X) <= grip, right = Math.Abs(x - box.Right) <= grip;
        bool top = Math.Abs(y - box.Y) <= grip, bottom = Math.Abs(y - box.Bottom) <= grip;
        bool withinX = x >= box.X - grip && x <= box.Right + grip;
        bool withinY = y >= box.Y - grip && y <= box.Bottom + grip;
        if (top && left) return BoxHandle.NW;
        if (top && right) return BoxHandle.NE;
        if (bottom && left) return BoxHandle.SW;
        if (bottom && right) return BoxHandle.SE;
        if (top && withinX) return BoxHandle.N;
        if (bottom && withinX) return BoxHandle.S;
        if (left && withinY) return BoxHandle.W;
        if (right && withinY) return BoxHandle.E;
        if (x > box.X && x < box.Right && y > box.Y && y < box.Bottom) return BoxHandle.Move;
        return BoxHandle.None;
    }

    /// <summary>
    /// What a press at the point grabs: a resize handle of the active box, else one of the other box, else the body of
    /// the smaller box containing the point (the active one when both are the same size), so each box can always be
    /// grabbed somewhere, else nothing (the active box, <see cref="BoxHandle.None"/>).
    /// </summary>
    public static (EditorBox Box, BoxHandle Handle) Pick(EditRect? ocr, EditRect? caption, EditorBox active,
        double x, double y, double grip) =>
        Pick([(EditorBox.Ocr, ocr), (EditorBox.Caption, caption)], active, x, y, grip);

    /// <summary>
    /// What a press at the point grabs among <paramref name="boxes"/> (a null rect is not there): a resize handle of the
    /// active box, else one of another box (in list order), else the body of the smallest box containing the point (the
    /// active one on a tie), so each box can always be grabbed somewhere, else nothing (the active box,
    /// <see cref="BoxHandle.None"/>).
    /// </summary>
    public static (EditorBox Box, BoxHandle Handle) Pick(IReadOnlyList<(EditorBox Box, EditRect? Rect)> boxes,
        EditorBox active, double x, double y, double grip)
    {
        // The active box first, so it wins both a shared handle and a tie between bodies.
        var hits = boxes.Where(b => b.Box == active).Concat(boxes.Where(b => b.Box != active))
            .Where(b => b.Rect is not null)
            .Select(b => (b.Box, Rect: b.Rect!.Value, Handle: HitHandle(b.Rect!.Value, x, y, grip)))
            .ToList();
        foreach (var hit in hits)
            if (hit.Handle is not (BoxHandle.None or BoxHandle.Move))
                return (hit.Box, hit.Handle);
        static double Area(EditRect r) => r.Width * r.Height;
        (EditorBox Box, double Area)? smallest = null;
        foreach (var hit in hits)
            if (hit.Handle == BoxHandle.Move && (smallest is null || Area(hit.Rect) < smallest.Value.Area))
                smallest = (hit.Box, Area(hit.Rect));
        return smallest is { } s ? (s.Box, BoxHandle.Move) : (active, BoxHandle.None);
    }

    /// <summary>
    /// Moves (<see cref="BoxHandle.Move"/>, staying wholly inside the bounds) or resizes (only the grabbed edges move,
    /// clamped to the bounds and stopping at the minimum size) <paramref name="start"/> by the pointer delta.
    /// </summary>
    public static EditRect Drag(EditRect start, BoxHandle handle, double dx, double dy,
        double boundsWidth, double boundsHeight, double minWidth, double minHeight)
    {
        if (handle == BoxHandle.None)
            return start;
        if (handle == BoxHandle.Move)
        {
            double x = Math.Clamp(start.X + dx, 0, Math.Max(boundsWidth - start.Width, 0));
            double y = Math.Clamp(start.Y + dy, 0, Math.Max(boundsHeight - start.Height, 0));
            return start with { X = x, Y = y };
        }
        double left = start.X, top = start.Y, right = start.Right, bottom = start.Bottom;
        if (handle is BoxHandle.W or BoxHandle.NW or BoxHandle.SW)
            left = Math.Clamp(left + dx, 0, Math.Max(right - minWidth, 0));
        if (handle is BoxHandle.E or BoxHandle.NE or BoxHandle.SE)
            right = Math.Clamp(right + dx, Math.Min(left + minWidth, boundsWidth), boundsWidth);
        if (handle is BoxHandle.N or BoxHandle.NW or BoxHandle.NE)
            top = Math.Clamp(top + dy, 0, Math.Max(bottom - minHeight, 0));
        if (handle is BoxHandle.S or BoxHandle.SW or BoxHandle.SE)
            bottom = Math.Clamp(bottom + dy, Math.Min(top + minHeight, boundsHeight), boundsHeight);
        return new EditRect(left, top, right - left, bottom - top);
    }

    /// <summary>How far (DIP, either axis) the pointer must travel before a press moves or resizes a box.</summary>
    public const double DragThreshold = 3;

    /// <summary>Whether a pointer travel is a drag, not the jitter of a click (which only selects a box).</summary>
    public static bool IsDrag(double dx, double dy) => Math.Abs(dx) >= DragThreshold || Math.Abs(dy) >= DragThreshold;

    /// <summary>The normalized rectangle of a free drag, clamped to the bounds.</summary>
    public static EditRect FromDrag(double x0, double y0, double x1, double y1, double boundsWidth, double boundsHeight)
    {
        x0 = Math.Clamp(x0, 0, boundsWidth);
        x1 = Math.Clamp(x1, 0, boundsWidth);
        y0 = Math.Clamp(y0, 0, boundsHeight);
        y1 = Math.Clamp(y1, 0, boundsHeight);
        return new EditRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));
    }
}
