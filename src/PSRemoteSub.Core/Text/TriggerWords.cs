using System.Globalization;
using System.Text;
using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Text;

/// <summary>Trigger words typed by the user, and the tolerant match against OCR lines.</summary>
public static class TriggerWords
{
    private const int FuzzyMinLetters = 4;

    public static IReadOnlyList<string> Parse(string? typed) =>
        string.IsNullOrWhiteSpace(typed)
            ? []
            : typed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static bool Matches(IReadOnlyList<OcrLine> lines, IReadOnlyList<string> words)
    {
        if (words.Count == 0 || lines.Count == 0) return false;

        var tokens = lines.SelectMany(l => Tokenize(l.Text)).ToList();
        string? joined = null;

        foreach (var word in words)
        {
            var w = word.Trim();
            if (w.Length == 0) continue;

            if (!w.All(IsTokenChar))
            {
                // A phrase, or a word with punctuation: it can't equal a letter/digit token, so look for it in the text.
                joined ??= Collapse(string.Join(' ', lines.Select(l => l.Text)));
                if (joined.Contains(Collapse(w), StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }

            var lower = w.ToLowerInvariant();
            var fuzzy = w.Length >= FuzzyMinLetters;
            foreach (var token in tokens)
            {
                if (string.Equals(token, w, StringComparison.OrdinalIgnoreCase)) return true;
                if (fuzzy && WithinOneEdit(token.ToLowerInvariant(), lower)) return true;
            }
        }
        return false;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (IsTokenChar(ch)) { sb.Append(ch); continue; }
            if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>Letters, digits and combining marks (Thai vowel and tone marks, accents) belong to a word.</summary>
    private static bool IsTokenChar(char ch) =>
        char.IsLetterOrDigit(ch) || char.GetUnicodeCategory(ch) is
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static string Collapse(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Levenshtein distance ≤ 1, bailing out as soon as it must exceed that.</summary>
    private static bool WithinOneEdit(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (rowMin > 1) return false;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length] <= 1;
    }
}
