namespace PSRemoteSub.Core.Translation.Ai;

/// <summary>
/// Strips the wrappers models add around a translation (a leading reasoning block, code fences, quotes,
/// "Translation:" labels) and rejects answers too long to be a translation of the line.
/// </summary>
public static class AiResponseCleaner
{
    private const string ThinkOpen = "<think>";
    private const string ThinkClose = "</think>";
    private const int MaxPasses = 3;

    private static readonly string[] Prefixes = ["Translation:", "คำแปล:"];

    private static readonly (char Open, char Close)[] QuotePairs =
        [('"', '"'), ('“', '”'), ('「', '」'), ('『', '』'), ('«', '»'), ('`', '`')];

    /// <summary>The cleaned answer, or null when nothing usable is left (empty, or reasoning that never closed).</summary>
    public static string? Clean(string? raw)
    {
        if (raw is null) return null;
        var s = raw.Trim();

        // Reasoning models (for example on Ollama) may put their thinking first. An unclosed block means the answer
        // was cut off inside it, so there is no translation.
        if (s.StartsWith(ThinkOpen, StringComparison.OrdinalIgnoreCase))
        {
            int end = s.IndexOf(ThinkClose, StringComparison.OrdinalIgnoreCase);
            if (end < 0) return null;
            s = s[(end + ThinkClose.Length)..].Trim();
        }
        int open = s.IndexOf(ThinkOpen, StringComparison.OrdinalIgnoreCase);
        if (open >= 0 && s.IndexOf(ThinkClose, open, StringComparison.OrdinalIgnoreCase) < 0)
            return null;

        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = s.IndexOf('\n');
            s = nl < 0 ? s[3..] : s[(nl + 1)..];
            s = s.TrimEnd();
            if (s.EndsWith("```", StringComparison.Ordinal)) s = s[..^3];
            s = s.Trim();
        }

        // Labels and quotes can nest either way round ("Translation: x" in quotes, or a label before a quoted line).
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            var before = s;
            s = StripPrefix(s);
            s = StripQuotes(s);
            if (s == before) break;
        }

        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// False when <paramref name="answer"/> is far longer than any translation of <paramref name="source"/> could be
    /// (a model that explained, repeated itself or ran on): more than 6 × the source length + 40 characters.
    /// </summary>
    public static bool IsPlausible(string answer, string source)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(source);
        return answer.Length <= 6 * source.Length + 40;
    }

    /// <summary>Latin words on one answer line, with no other script on it, that make it read as a sentence of notes.</summary>
    private const int CommentaryWords = 6;

    private static readonly string[] Markers = ["[translate]", "[previous]"];

    /// <summary>
    /// True when the model wrote about the translation instead of only translating: a prompt marker
    /// (<c>[translate]</c>) is echoed, or the answer is in another script (Thai) but has a line of at least six Latin
    /// words with none of that script that is not copied from <paramref name="source"/> (for example
    /// "Here's the translation with line breaks kept:"). Names and short English words inside a Thai line are fine;
    /// an all-Latin answer is left to the other checks.
    /// </summary>
    public static bool HasCommentary(string answer, string source)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(source);
        foreach (var marker in Markers)
            if (answer.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        if (!answer.Any(IsOtherScriptLetter))
            return false;
        foreach (var raw in answer.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Any(IsOtherScriptLetter) || LatinWords(line) < CommentaryWords)
                continue;
            if (!source.Contains(line, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsOtherScriptLetter(char c) => char.IsLetter(c) && !IsLatin(c);

    private static bool IsLatin(char c) => c < 0x250 || c is >= 'Ḁ' and <= 'ỿ';

    private static int LatinWords(string line)
    {
        int words = 0;
        bool inWord = false;
        foreach (var c in line)
        {
            bool letter = char.IsLetter(c) && IsLatin(c);
            if (letter && !inWord)
                words++;
            inWord = letter || (inWord && c is '\'' or '’');
        }
        return words;
    }

    private static string StripPrefix(string s)
    {
        foreach (var p in Prefixes)
        {
            if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return s[p.Length..].TrimStart();
        }
        return s;
    }

    /// <summary>Strips one wrapping pair only when it wraps the whole line; inner quotes stay.</summary>
    private static string StripQuotes(string s)
    {
        foreach (var (open, close) in QuotePairs)
        {
            if (s.Length >= 2 && s[0] == open && s[^1] == close
                && s.IndexOf(close, 1) == s.Length - 1)
                return s[1..^1].Trim();
        }
        return s;
    }
}
