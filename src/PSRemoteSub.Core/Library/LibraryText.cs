using System.Text;
using System.Text.RegularExpressions;

namespace PSRemoteSub.Core.Library;

/// <summary>Text helpers shared by the translation library (same rules as LeonidaSubTH).</summary>
public static class LibraryText
{
    private static readonly Regex SentenceBreak = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);

    private static readonly HashSet<string> NegationTokens = new(StringComparer.Ordinal)
    {
        "not", "never", "dont", "doesnt", "didnt", "isnt", "arent", "wasnt", "werent",
        "havent", "hasnt", "hadnt", "cant", "cannot", "couldnt", "wouldnt", "shouldnt",
        "wont", "mustnt", "aint",
    };

    /// <summary>Lowercase ASCII words and apostrophes only, single-spaced; non-ASCII becomes a separator.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var raw in s)
        {
            char c = raw is '\u2019' or '\u2018' ? '\'' : raw;
            if (c > 127) { sb.Append(' '); continue; }
            c = char.ToLowerInvariant(c);
            bool keep = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '\'';
            sb.Append(keep ? c : ' ');
        }
        var tokens = sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('\''))
            .Where(t => t.Length > 0);
        return string.Join(' ', tokens);
    }

    /// <summary>Split after one or more of . ! ? followed by whitespace; leading dashes are trimmed.</summary>
    public static IReadOnlyList<string> SplitSentences(string s)
    {
        var result = new List<string>();
        foreach (var piece in SentenceBreak.Split(s))
        {
            var t = piece.TrimStart('-', '\u2013', ' ', '\t', '\r', '\n').Trim();
            if (t.Length > 0) result.Add(t);
        }
        return result;
    }

    /// <summary>True when a normalized line contains a negation word.</summary>
    public static bool IsNegated(string normalized)
    {
        foreach (var t in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (t.EndsWith("n't", StringComparison.Ordinal) || NegationTokens.Contains(t)) return true;
        return false;
    }

    /// <summary>
    /// True when two normalized lines hold the same tokens with a digit in them (as a multiset, order aside): an OCR
    /// slip in a word is fine, a different number or amount is a different line.
    /// </summary>
    public static bool SameDigitTokens(string a, string b) => DigitTokens(a).SequenceEqual(DigitTokens(b));

    private static List<string> DigitTokens(string normalized)
    {
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Any(char.IsAsciiDigit))
            .ToList();
        tokens.Sort(StringComparer.Ordinal);
        return tokens;
    }

    /// <summary>Indel similarity 0-100: round(100 * 2 * LCS / (len a + len b)); 100 for two empty strings.</summary>
    public static int IndelRatio(string a, string b)
    {
        int total = a.Length + b.Length;
        return total == 0 ? 100 : IndelRatio(Lcs(a, b), total);
    }

    /// <summary><see cref="IndelRatio(string, string)"/> from a known LCS and the two lengths' sum (not 0).</summary>
    internal static int IndelRatio(int lcs, int total) =>
        (int)Math.Round(100.0 * 2 * lcs / total, MidpointRounding.AwayFromZero);

    /// <summary>Exact floor check in integer math: 200 * LCS >= floor * (len a + len b).</summary>
    internal static bool MeetsFloor(string a, string b, int floor) => MeetsFloor(Lcs(a, b), a.Length + b.Length, floor);

    /// <summary><see cref="MeetsFloor(string, string, int)"/> from a known LCS and the two lengths' sum.</summary>
    internal static bool MeetsFloor(int lcs, int total, int floor) => 200L * lcs >= (long)floor * total;

    /// <summary>
    /// Length of the longest common subsequence. ASCII strings use the bit-parallel form (Allison-Dix / Hyyrö: one
    /// pass over the longer string, the shorter one as bit masks); anything else uses the table.
    /// </summary>
    internal static int Lcs(string a, string b)
    {
        if (a.Length > b.Length) (a, b) = (b, a);
        if (a.Length == 0) return 0;
        if (!Ascii.IsValid(a) || !Ascii.IsValid(b)) return LcsTable(a, b);
        int words = (a.Length + 63) >> 6;
        Span<ulong> peq = words <= 8 ? stackalloc ulong[128 * 8] : new ulong[128 * words];
        peq = peq[..(128 * words)];
        peq.Clear();
        for (int i = 0; i < a.Length; i++) peq[a[i] * words + (i >> 6)] |= 1UL << i;
        Span<ulong> v = words <= 8 ? stackalloc ulong[8] : new ulong[words];
        v = v[..words];
        v.Fill(ulong.MaxValue);
        foreach (var ch in b)
        {
            var m = peq.Slice(ch * words, words);
            ulong carry = 0;
            for (int w = 0; w < words; w++)
            {
                // V = (V + (V & M)) | (V & ~M), the addition carrying across words.
                ulong vw = v[w];
                ulong sum = vw + (vw & m[w]);
                ulong next = sum < vw ? 1UL : 0UL;
                ulong total = sum + carry;
                if (total < sum) next = 1;
                carry = next;
                v[w] = total | (vw & ~m[w]);
            }
        }
        int ones = 0;
        for (int w = 0; w < words; w++)
        {
            int bits = Math.Min(64, a.Length - (w << 6));
            ulong mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
            ones += System.Numerics.BitOperations.PopCount(v[w] & mask);
        }
        return a.Length - ones; // the zero bits
    }

    private static int LcsTable(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
