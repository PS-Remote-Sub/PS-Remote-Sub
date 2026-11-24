using PSRemoteSub.Core.Layout;

namespace PSRemoteSub.Core.Tests.Layout;

public class FontFitTests
{
    // Three lines of text: content height is three times the font size.
    private static int ThreeLines(double size) => (int)(size * 3);

    [Fact] public void Fits_AtPreferred_ReturnsPreferred() =>
        Assert.Equal(28, FontFit.Choose(28, 100, ThreeLines));

    [Fact] public void TooTall_StepsDownByOne() =>
        Assert.Equal(25, FontFit.Choose(28, 75, ThreeLines));

    [Fact] public void NeverBelow16() =>
        Assert.Equal(16, FontFit.Choose(28, 10, ThreeLines));

    [Fact] public void PreferredBelow16_IsTheMinimum() =>
        Assert.Equal(12, FontFit.Choose(12, 10, ThreeLines));

    [Fact] public void FractionalPreferred_TriesItFirst() =>
        Assert.Equal(27.5, FontFit.Choose(28.5, 84, ThreeLines));

    [Fact] public void MinimumIs16() => Assert.Equal(16, FontFit.MinFitFontSize);
}
