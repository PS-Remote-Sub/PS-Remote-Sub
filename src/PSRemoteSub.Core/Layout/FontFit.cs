namespace PSRemoteSub.Core.Layout;

/// <summary>Picks the font size that lets a caption fit the height of its box.</summary>
public static class FontFit
{
    /// <summary>Smallest size the font is shrunk to; past it the box grows instead.</summary>
    public const double MinFitFontSize = 16;

    /// <summary>
    /// The largest size from <paramref name="preferred"/> down, in steps of 1, whose content fits
    /// <paramref name="boxHeightPx"/>; never below <see cref="MinFitFontSize"/> (or <paramref name="preferred"/> when it is
    /// smaller), which is returned when nothing fits.
    /// </summary>
    /// <param name="contentHeightAt">Content height in pixels at a font size, measured at the box's width.</param>
    public static double Choose(double preferred, int boxHeightPx, Func<double, int> contentHeightAt)
    {
        ArgumentNullException.ThrowIfNull(contentHeightAt);
        double min = Math.Min(MinFitFontSize, preferred);
        for (double size = preferred; size >= min; size--)
            if (contentHeightAt(size) <= boxHeightPx)
                return size;
        return min;
    }
}
