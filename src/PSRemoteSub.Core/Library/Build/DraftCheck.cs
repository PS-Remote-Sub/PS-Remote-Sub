namespace PSRemoteSub.Core.Library.Build;

public sealed record DraftResult(bool Ok, IReadOnlyList<string> Problems, IReadOnlyList<(string Id, string Th)> Rows);

/// <summary>Validates an agent's draft (<c>id TAB thai</c> per line) against its chunk. Every problem names an id or a line.</summary>
public static class DraftCheck
{
    public static DraftResult Check(Chunk chunk, string draft)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(draft);
        var byId = chunk.Lines.ToDictionary(l => l.Id, StringComparer.Ordinal);
        var problems = new List<string>();
        var got = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var lines = draft.TrimStart('﻿').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0)
            {
                problems.Add($"line {i + 1}: not \"<id><TAB><thai>\"");
                continue;
            }
            var id = line[..tab].Trim();
            var th = line[(tab + 1)..];
            if (!byId.ContainsKey(id)) { problems.Add($"{id}: unknown id (line {i + 1})"); continue; }
            if (!seen.Add(id)) { problems.Add($"{id}: duplicate (line {i + 1})"); continue; }
            if (th.Contains('\t', StringComparison.Ordinal)) { problems.Add($"{id}: translation contains a TAB"); continue; }
            th = th.Trim();
            var problem = Content(byId[id].En, th);
            if (problem is not null) problems.Add($"{id}: {problem}");
            else got[id] = th;
        }

        foreach (var l in chunk.Lines)
            if (!seen.Contains(l.Id)) problems.Add($"{l.Id}: missing");

        var rows = chunk.Lines.Where(l => got.ContainsKey(l.Id)).Select(l => (l.Id, got[l.Id])).ToList();
        return new DraftResult(problems.Count == 0, problems, rows);
    }

    /// <summary>Language tags the game puts before translated foreign speech, and the Thai prefix translation-guide.md asks for.</summary>
    internal static readonly IReadOnlyList<(string En, string Th)> LanguagePrefixes =
    [
        ("In Spanish:", "(ภาษาสเปน)"), ("In Creole:", "(ภาษาครีโอล)"), ("In Filipino:", "(ภาษาฟิลิปปินส์)"),
        ("In Italian:", "(ภาษาอิตาลี)"), ("In Russian:", "(ภาษารัสเซีย)"), ("In French:", "(ภาษาฝรั่งเศส)"),
        ("In Albanian:", "(ภาษาแอลเบเนีย)"), ("In Portuguese:", "(ภาษาโปรตุเกส)"), ("In Serbian:", "(ภาษาเซอร์เบีย)"),
        ("Unknown Language:", "(ภาษาอื่น)"),
    ];

    private static string? Content(string en, string th)
    {
        if (th.Length == 0) return "empty translation";
        if (th.Contains('�', StringComparison.Ordinal)) return "broken character (U+FFFD) in translation";
        var tag = LanguagePrefixes.FirstOrDefault(p => en.StartsWith(p.En, StringComparison.OrdinalIgnoreCase));
        if (tag.Th is not null && !th.StartsWith(tag.Th, StringComparison.Ordinal))
            return $"must start with {tag.Th} because the English starts with {tag.En}";
        if (tag.Th is null && LanguagePrefixes.Any(p => th.StartsWith(p.Th, StringComparison.Ordinal)))
            return "has a language prefix the English line does not have (shifted line?)";
        var words = LibraryText.Normalize(en).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words > 2 && !th.Any(c => c is >= '฀' and <= '๿')) return "no Thai letters (not translated?)";
        var lost = SubtitleMarkup.KeptTokens(en).Where(t => !th.Contains(t, StringComparison.Ordinal)).ToList();
        if (lost.Count > 0) return "lost token(s) " + string.Join(" ", lost);
        if (th.Length > Math.Max(4 * en.Length, 40)) return "translation too long (explanation added?)";
        return null;
    }
}
