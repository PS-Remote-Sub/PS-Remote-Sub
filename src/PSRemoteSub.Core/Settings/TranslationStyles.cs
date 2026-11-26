namespace PSRemoteSub.Core.Settings;

public enum TranslationStyle { Fast, Balanced, Context }

/// <summary>Named presets over the AI context lines, waiting queue and parallel requests.</summary>
public static class TranslationStyles
{
    public static (int Context, int Queue, int Parallel) Values(TranslationStyle style) => style switch
    {
        TranslationStyle.Fast => (1, 2, 1),
        TranslationStyle.Balanced => (4, 3, 1),
        TranslationStyle.Context => (8, 4, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(style)),
    };

    /// <summary>The preset these settings match exactly, or null (custom).</summary>
    public static TranslationStyle? Detect(TranslationSettings t)
    {
        var now = (t.ContextLines, t.MaxWaitingLines, t.ParallelRequests);
        foreach (var style in Enum.GetValues<TranslationStyle>())
            if (Values(style) == now) return style;
        return null;
    }

    public static void Apply(TranslationSettings t, TranslationStyle style)
    {
        (t.ContextLines, t.MaxWaitingLines, t.ParallelRequests) = Values(style);
    }
}
