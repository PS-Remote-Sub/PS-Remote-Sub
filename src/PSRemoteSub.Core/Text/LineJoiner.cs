using System.Text;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Languages;

namespace PSRemoteSub.Core.Text;

public static class LineJoiner
{
    public static string Join(IReadOnlyList<OcrLine> lines, SourceLanguage lang) =>
        JoinParts(lines.OrderBy(l => l.Bounds.Y).ThenBy(l => l.Bounds.X).Select(l => l.Text), lang);

    /// <summary>Joins wrapped parts of one sentence: with spaces, or for ja/zh without the spaces OCR puts between CJK.</summary>
    public static string JoinParts(IEnumerable<string> parts, SourceLanguage lang)
    {
        if (lang.Code is "ja" or "zh")
            return RemoveCjkSpaces(string.Concat(parts.Select(RemoveCjkSpaces)));
        return string.Join(' ', parts);
    }

    private static string RemoveCjkSpaces(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c))
            {
                int p = i - 1;
                while (p >= 0 && char.IsWhiteSpace(s[p])) p--;
                int n = i + 1;
                while (n < s.Length && char.IsWhiteSpace(s[n])) n++;
                if (p >= 0 && n < s.Length && CjkChars.IsCjk(s[p]) && CjkChars.IsCjk(s[n])) continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
