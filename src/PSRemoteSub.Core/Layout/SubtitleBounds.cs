using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Text;

namespace PSRemoteSub.Core.Layout;

/// <summary>Where the game drew a subtitle, so the overlay can cover it exactly.</summary>
public static class SubtitleBounds
{
    /// <summary>Margin around the text, so the game's backing strip is hidden too: a quarter line, at least this.</summary>
    public const int MinMarginPx = 4;

    /// <summary>
    /// The union of <paramref name="lines"/> as a fraction of the frame, with a margin, or null when there are none.
    /// </summary>
    /// <param name="lines">OCR lines, in OCR-image pixels.</param>
    /// <param name="ocrScale">OCR image width over capture crop width (the preprocessor may upscale).</param>
    /// <param name="regionPx">The crop the OCR image was made from, in frame pixels.</param>
    public static RegionFraction? FromOcr(IReadOnlyList<OcrLine> lines, double ocrScale, PixelRect regionPx,
        int frameWidth, int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (frameWidth <= 0 || frameHeight <= 0 || ocrScale <= 0)
            return null;

        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        double heights = 0;
        int count = 0;
        foreach (var line in lines)
        {
            // Only lines that are part of the subtitle text: UI-edge noise ("|", "_") is dropped from the text, and a
            // line without word boxes has no real position. Either would pull the box away from the subtitle.
            if (line.Bounds.Width <= 0 || line.Bounds.Height <= 0 || TextNormalizer.Normalize(line.Text).Length == 0)
                continue;
            count++;
            var b = line.Bounds;
            left = Math.Min(left, b.X / ocrScale);
            top = Math.Min(top, b.Y / ocrScale);
            right = Math.Max(right, b.Right / ocrScale);
            bottom = Math.Max(bottom, b.Bottom / ocrScale);
            heights += b.Height / ocrScale;
        }
        if (count == 0)
            return null;
        int margin = Math.Max(MinMarginPx, (int)Math.Round(heights / count / 4));
        return ToFrame(left, top, right, bottom, margin, regionPx, frameWidth, frameHeight);
    }

    /// <summary>
    /// Where the game drew each of <paramref name="reads"/> (one per caption row), with the same margin as
    /// <see cref="FromOcr"/>; null when there are no reads, a read has no <see cref="SpeakerRead.Box"/>, or no line counts.
    /// </summary>
    public static IReadOnlyList<RegionFraction>? ForRows(IReadOnlyList<SpeakerRead> reads, IReadOnlyList<OcrLine> lines,
        double ocrScale, PixelRect regionPx, int frameWidth, int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(lines);
        if (reads.Count == 0 || frameWidth <= 0 || frameHeight <= 0 || ocrScale <= 0 || reads.Any(r => r.Box is null))
            return null;
        double heights = 0;
        int count = 0;
        foreach (var line in lines)
        {
            if (line.Bounds.Width <= 0 || line.Bounds.Height <= 0 || TextNormalizer.Normalize(line.Text).Length == 0)
                continue;
            count++;
            heights += line.Bounds.Height / ocrScale;
        }
        if (count == 0)
            return null;
        int margin = Math.Max(MinMarginPx, (int)Math.Round(heights / count / 4));
        return [.. reads.Select(r => r.Box!.Value).Select(b => ToFrame(b.X / ocrScale, b.Y / ocrScale, b.Right / ocrScale,
            b.Bottom / ocrScale, margin, regionPx, frameWidth, frameHeight))];
    }

    /// <summary>A box in capture-crop pixels, grown by <paramref name="margin"/>, as a fraction of the frame (clipped to it).</summary>
    private static RegionFraction ToFrame(double left, double top, double right, double bottom, int margin,
        PixelRect regionPx, int frameWidth, int frameHeight)
    {
        int x0 = Math.Clamp(regionPx.X + (int)Math.Floor(left) - margin, 0, frameWidth - 1);
        int y0 = Math.Clamp(regionPx.Y + (int)Math.Floor(top) - margin, 0, frameHeight - 1);
        int x1 = Math.Clamp(regionPx.X + (int)Math.Ceiling(right) + margin, x0 + 1, frameWidth);
        int y1 = Math.Clamp(regionPx.Y + (int)Math.Ceiling(bottom) + margin, y0 + 1, frameHeight);
        return RegionFraction.FromPixels(new PixelRect(x0, y0, x1 - x0, y1 - y0), frameWidth, frameHeight);
    }
}
