using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Layout;

public static class OverlayLayout
{
    /// <summary>
    /// Custom: the caption box in physical screen pixels. Content taller than the box grows it upwards only (bottom
    /// fixed), clamped at the client top, where it keeps the full content height so the text is never cut; never taller
    /// than the client area. The width is always the box's.
    /// </summary>
    public static PixelRect ComputeCaption(PixelRect clientScreen, RegionFraction captionBox, int contentHeightPx)
    {
        var box = captionBox.ToPixels(clientScreen.Width, clientScreen.Height).Offset(clientScreen.X, clientScreen.Y);
        int height = Math.Min(Math.Max(box.Height, Math.Max(contentHeightPx, 1)), clientScreen.Height);
        int y = Math.Max(box.Bottom - height, clientScreen.Y);
        return new PixelRect(box.X, y, box.Width, height);
    }

    /// <summary>
    /// Fit with one box per caption row: each row's box covers the game's line under it (<paramref name="english"/>),
    /// and grows about that line's centre when its text (<paramref name="content"/>) needs more room. Rows closer than
    /// <paramref name="gapPx"/> make the gap from the room their text leaves (first half each, then whichever has room),
    /// and only then is the lower row moved down. Shifted (not shrunk) to stay inside the client area. Physical pixels.
    /// </summary>
    /// <param name="english">Each row's game line, top to bottom, in screen pixels.</param>
    /// <param name="content">Each row's text size with padding, in screen pixels.</param>
    public static IReadOnlyList<PixelRect> ComputeFitRows(PixelRect clientScreen, IReadOnlyList<PixelRect> english,
        IReadOnlyList<(int Width, int Height)> content, int gapPx)
    {
        ArgumentNullException.ThrowIfNull(english);
        ArgumentNullException.ThrowIfNull(content);
        if (english.Count != content.Count)
            throw new ArgumentException("One content size per row.", nameof(content));
        var rows = new PixelRect[english.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var e = english[i];
            int w = Math.Min(Math.Max(e.Width, content[i].Width), clientScreen.Width);
            int h = Math.Max(e.Height, content[i].Height);
            int x = Math.Clamp(e.X + (e.Width - w) / 2, clientScreen.X, clientScreen.Right - w);
            int y = e.Y + (e.Height - h) / 2;
            if (i > 0)
            {
                var prev = rows[i - 1];
                int need = prev.Bottom + gapPx - y;
                if (need > 0)
                {
                    int prevRoom = Math.Max(prev.Height - content[i - 1].Height, 0);
                    int room = Math.Max(h - content[i].Height, 0);
                    int fromPrev = Math.Min(prevRoom, need / 2);
                    int fromThis = Math.Min(room, need - fromPrev);
                    fromPrev += Math.Min(prevRoom - fromPrev, need - fromPrev - fromThis);
                    rows[i - 1] = prev with { Height = prev.Height - fromPrev };
                    y += fromThis;
                    h -= fromThis;
                    y += need - fromPrev - fromThis;
                }
            }
            rows[i] = new PixelRect(x, y, w, h);
        }
        return rows;
    }

    /// <summary>
    /// Fit: a box centred on the game's subtitle and never smaller than it. Wider content grows it both ways up to the
    /// region's width; a previous line adds height above only, so the latest line stays over the original. Shifted
    /// (not shrunk) to stay inside the client area. All sizes in physical pixels.
    /// </summary>
    /// <param name="textBounds">Where the game drew the subtitle, as a fraction of the client area.</param>
    /// <param name="contentWidthPx">Natural width of the content (with padding) at the region's width.</param>
    /// <param name="captionHeightPx">Height of the latest line (with padding).</param>
    /// <param name="previousHeightPx">Height the previous line adds (0 when hidden).</param>
    public static PixelRect ComputeFit(PixelRect clientScreen, RegionFraction region, RegionFraction textBounds,
        int contentWidthPx, int captionHeightPx, int previousHeightPx)
    {
        var t = textBounds.ToPixels(clientScreen.Width, clientScreen.Height).Offset(clientScreen.X, clientScreen.Y);
        var r = region.ToPixels(clientScreen.Width, clientScreen.Height);

        int width = Math.Min(Math.Max(t.Width, contentWidthPx), Math.Max(r.Width, t.Width));
        int captionHeight = Math.Max(t.Height, Math.Max(captionHeightPx, 1));
        int previous = Math.Max(previousHeightPx, 0);
        int x = t.X + (t.Width - width) / 2;
        int y = t.Y + (t.Height - captionHeight) / 2 - previous;

        width = Math.Min(width, clientScreen.Width);
        int height = Math.Min(captionHeight + previous, clientScreen.Height);
        x = Math.Clamp(x, clientScreen.X, clientScreen.Right - width);
        y = Math.Clamp(y, clientScreen.Y, clientScreen.Bottom - height);
        return new PixelRect(x, y, width, height);
    }
}
