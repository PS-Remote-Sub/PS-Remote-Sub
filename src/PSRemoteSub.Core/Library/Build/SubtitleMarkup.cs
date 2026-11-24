using System.Text.RegularExpressions;

namespace PSRemoteSub.Core.Library.Build;

/// <summary>Game subtitle markup: timing marks split one row into on-screen pieces; display marks are dropped; button/placeholder tokens are kept.</summary>
public static partial class SubtitleMarkup
{
    [GeneratedRegex(@"~(?:sl|lr):[^~]*~")]
    private static partial Regex Timing();

    [GeneratedRegex(@"~(?:t|Tone|COLOR_[A-Z_]+)~")]
    private static partial Regex Display();

    [GeneratedRegex(@"~[^~\s]+~")]
    private static partial Regex AnyToken();

    public static IReadOnlyList<string> Pieces(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Timing().Split(text)
            .Select(p => Display().Replace(p, "").Trim())
            .Where(p => p.Length > 0)
            .ToList();
    }

    public static IReadOnlyList<string> KeptTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return AnyToken().Matches(text)
            .Select(m => m.Value)
            .Where(v => !Timing().IsMatch(v) && !Display().IsMatch(v))
            .ToList();
    }
}
