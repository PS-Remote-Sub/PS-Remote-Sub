using PSRemoteSub.Core.Languages;

namespace PSRemoteSub.Core.Text;

public static class TextNormalizer
{
    private static bool IsNoiseToken(string token) =>
        token.All(c => c is '|' or '_' or '~' or '`');

    /// <summary>Collapses whitespace and drops UI-edge noise tokens, line by line; empty lines are dropped, <c>\n</c> kept.</summary>
    public static string Normalize(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return string.Join('\n', raw.Split('\n').Select(NormalizeLine).Where(l => l.Length > 0));
    }

    private static string NormalizeLine(string line) =>
        string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(t => !IsNoiseToken(t)));

    public static bool HasSubtitle(string normalized, SourceLanguage lang)
    {
        Func<char, bool> inScript = lang.Code switch
        {
            "en" => CjkChars.IsLatinLetter,
            "ja" => c => CjkChars.IsHiragana(c) || CjkChars.IsKatakana(c) || CjkChars.IsIdeograph(c),
            "zh" => CjkChars.IsIdeograph,
            "ko" => CjkChars.IsHangul,
            _ => char.IsLetter,
        };
        return normalized.Count(inScript) >= 2;
    }
}

internal static class CjkChars
{
    public static bool IsLatinLetter(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');
    public static bool IsHiragana(char c) => c is >= '\u3040' and <= '\u309F';
    public static bool IsKatakana(char c) => c is >= '\u30A0' and <= '\u30FF';
    public static bool IsIdeograph(char c) => c is (>= '\u4E00' and <= '\u9FFF') or (>= '\u3400' and <= '\u4DBF');
    public static bool IsHangul(char c) => c is (>= '\uAC00' and <= '\uD7AF') or (>= '\u1100' and <= '\u11FF') or (>= '\u3130' and <= '\u318F');
    public static bool IsCjk(char c) => IsHiragana(c) || IsKatakana(c) || IsIdeograph(c);
}
