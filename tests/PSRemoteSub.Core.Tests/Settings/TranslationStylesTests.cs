using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Tests.Settings;

public sealed class TranslationStylesTests
{
    private static TranslationSettings Of(int ctx, int queue, int par) =>
        new() { ContextLines = ctx, MaxWaitingLines = queue, ParallelRequests = par };

    [Fact]
    public void Detect_KnownCombinations()
    {
        Assert.Equal(TranslationStyle.Balanced, TranslationStyles.Detect(Of(4, 3, 1)));
        Assert.Equal(TranslationStyle.Fast, TranslationStyles.Detect(Of(1, 2, 1)));
        Assert.Equal(TranslationStyle.Context, TranslationStyles.Detect(Of(8, 4, 1)));
        Assert.Null(TranslationStyles.Detect(Of(5, 3, 1)));
    }

    [Fact]
    public void Apply_Fast_Sets121()
    {
        var t = Of(9, 9, 3);
        TranslationStyles.Apply(t, TranslationStyle.Fast);
        Assert.Equal((1, 2, 1), (t.ContextLines, t.MaxWaitingLines, t.ParallelRequests));
        Assert.Equal((8, 4, 1), TranslationStyles.Values(TranslationStyle.Context));
    }
}
