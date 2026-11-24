namespace PSRemoteSub.Core.Text;

/// <summary>Line handling for subtitles with one line per speaker: line i belongs to speaker i.</summary>
public static class SpeakerText
{
    /// <summary>Lines trimmed, carriage returns and blank lines dropped, joined with <c>\n</c>.</summary>
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Join('\n', text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    /// <summary>Non-blank lines in <paramref name="text"/>.</summary>
    public static int LineCount(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Split('\n').Count(l => l.Trim().Length > 0);
    }
}
