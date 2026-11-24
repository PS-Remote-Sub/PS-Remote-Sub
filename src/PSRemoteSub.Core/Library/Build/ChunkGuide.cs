using System.Text;

namespace PSRemoteSub.Core.Library.Build;

/// <summary>The guide an agent reads before translating one chunk: style, the people and terms that chunk touches.</summary>
public static class ChunkGuide
{
    private static readonly string[] AlwaysPeople = ["Jason", "Lucia"];

    public static string Render(Chunk chunk, IReadOnlyList<GlossaryTerm> glossary, GameInfo info, string style)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(glossary);
        ArgumentNullException.ThrowIfNull(info);
        var text = string.Join("\n", chunk.Lines.Select(l => l.En));
        var chars = new CharacterIndex(info);

        var sb = new StringBuilder();
        var parts = chunk.Lines.Select(l => l.Part).Distinct(StringComparer.Ordinal);
        sb.Append("# Chunk ").Append(chunk.Name).Append(" — ").AppendLine(string.Join(", ", parts));
        sb.AppendLine("Lines come from the same game scene but are NOT in spoken order; translate each line on its own.");
        sb.AppendLine();
        sb.AppendLine("## Style guide").AppendLine((style ?? "").Trim()).AppendLine();

        var people = new List<GameCharacter>();
        foreach (var name in AlwaysPeople)
            if (info.Characters.FirstOrDefault(c => c.En == name) is { } c && !people.Contains(c)) people.Add(c);
        foreach (var label in chunk.Lines.Select(l => l.Speaker).Distinct(StringComparer.Ordinal))
            if (chars.Resolve(label) is { } c && !people.Contains(c)) people.Add(c);
        foreach (var c in chars.MentionedIn(text))
            if (!people.Contains(c)) people.Add(c);
        if (people.Count > 0)
        {
            sb.AppendLine("## Characters (keep names in English, except the Thai nicknames the style guide gives)");
            foreach (var c in people) sb.AppendLine("- " + FormatCharacter(c));
            sb.AppendLine();
        }

        var padded = " " + LibraryText.Normalize(text) + " ";
        var terms = glossary.Where(t =>
        {
            var en = LibraryText.Normalize(t.En);
            return en.Length > 0 && padded.Contains(" " + en + " ", StringComparison.Ordinal);
        }).ToList();
        if (terms.Count > 0)
        {
            sb.AppendLine("## Glossary");
            foreach (var t in terms)
                sb.AppendLine(string.IsNullOrWhiteSpace(t.Note) ? $"- {t.En} → {t.Th}" : $"- {t.En} → {t.Th} ({t.Note})");
            sb.AppendLine();
        }

        var places = chars.PlacesIn(text);
        if (places.Count > 0)
        {
            sb.AppendLine("## Places");
            foreach (var p in places)
            {
                var line = new StringBuilder("- ").Append(p.En).Append(':');
                if (!string.IsNullOrWhiteSpace(p.Th)) line.Append(' ').Append(p.Th);
                if (!string.IsNullOrWhiteSpace(p.Note)) line.Append(" — ").Append(p.Note);
                sb.AppendLine(line.ToString());
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    private static string FormatCharacter(GameCharacter c)
    {
        var tags = new[] { c.Gender, c.Self }.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var sb = new StringBuilder(c.En);
        if (tags.Count > 0) sb.Append(" (").Append(string.Join("; ", tags)).Append(')');
        if (!string.IsNullOrWhiteSpace(c.Note))
        {
            var note = c.Note.Trim();
            if (note.Length > GameBriefBuilder.NoteMax) note = note[..GameBriefBuilder.NoteMax];
            sb.Append(": ").Append(note);
        }
        return sb.ToString();
    }
}
