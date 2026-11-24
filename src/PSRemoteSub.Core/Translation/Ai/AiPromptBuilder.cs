using System.Text;
using PSRemoteSub.Core.Library;

namespace PSRemoteSub.Core.Translation.Ai;

public static class AiPromptBuilder
{
    public static (string System, string User) Build(AiTranslationRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Kind == AiRequestKind.Names)
            return BuildNames(r);

        var (head, tail) = BuildSystemParts(r);
        var sys = head + (r.Brief is { } b ? NotesHeader + b.Static + "\n" + b.PerLine + "\n" : "") + tail;
        return (sys, BuildUser(r));
    }

    internal const string NotesHeader = "Game notes (follow them; Thai is the target style):\n";

    /// <summary>The subtitle system prompt in two halves: the instructions, and the closing reply line. The game notes go between them.</summary>
    internal static (string Head, string Tail) BuildSystemParts(AiTranslationRequest r)
    {
        var sys = new StringBuilder();
        sys.Append($"You translate video game subtitles from {r.SourceLanguageName} to {r.TargetLanguageName}.\n");
        sys.Append("The text comes from OCR and may contain misread or missing characters; infer the intended words from context.\n");
        sys.Append($"Write natural, conversational {r.TargetLanguageName} that a player can read quickly. Keep it short.\n");
        sys.Append("Keep names of characters, places and items consistent with earlier lines; transliterate them rather than translating their meaning.\n");
        sys.Append("A subtitle may have several lines (one per speaker, or one sentence wrapped); translate every line and keep the same line breaks.\n");
        if (!string.IsNullOrWhiteSpace(r.GameTitle))
            sys.Append($"The game is \"{r.GameTitle}\".\n");
        var head = sys.ToString();
        const string tail = "Reply with only the translation of the subtitle after [translate], every line of it, with no quotes, notes or explanations.";
        return (head, tail);
    }

    private static string BuildUser(AiTranslationRequest r)
    {
        var user = new StringBuilder();
        foreach (var c in r.Context)
        {
            // One prompt line per earlier subtitle, even when it had several speakers.
            user.Append("[previous] ").Append(OneLine(c.Source));
            if (c.Translation is not null) user.Append(" => ").Append(OneLine(c.Translation));
            user.Append('\n');
        }
        // One line goes after the marker. Several lines go below it, with their count, so every one of them is plainly
        // part of the subtitle: with only the first line marked, models wondered aloud about the others.
        var lines = r.Text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count <= 1)
            user.Append("[translate] ").Append(lines.Count == 1 ? lines[0] : r.Text);
        else
            user.Append("[translate] ").Append(lines.Count).Append(" lines:\n").AppendJoin('\n', lines);
        return user.ToString();
    }

    private static (string System, string User) BuildNames(AiTranslationRequest r)
    {
        var sys = new StringBuilder();
        sys.Append($"These are names of characters who speak in a video game, in {r.SourceLanguageName}.\n");
        sys.Append($"Transliterate these character names into {r.TargetLanguageName} script, one per line, in the same order. ");
        sys.Append("Do not translate their meaning.\n");
        if (!string.IsNullOrWhiteSpace(r.GameTitle))
            sys.Append($"The game is \"{r.GameTitle}\".\n");
        sys.Append("Reply with only the names, one per line, with no numbering, quotes or notes.");
        return (sys.ToString(), r.Text);
    }

    private static string OneLine(string s) => s.Replace("\r", "").Replace("\n", " / ");
}
