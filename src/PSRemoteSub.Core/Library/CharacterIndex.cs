using System.Text.RegularExpressions;

namespace PSRemoteSub.Core.Library;

/// <summary>Resolves speaker labels to game characters and finds characters/places mentioned in text.</summary>
public sealed class CharacterIndex
{
    private static readonly Regex Parenthetical = new(@"\(.*?\)", RegexOptions.Compiled);

    private readonly Dictionary<string, GameCharacter> _whole = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GameCharacter?> _words = new(StringComparer.Ordinal); // null = ambiguous
    private readonly List<(string Padded, GameCharacter Character)> _mentionNames = [];
    private readonly List<(string Padded, GamePlace Place)> _placeNames = [];

    public CharacterIndex(GameInfo info)
    {
        foreach (var c in info.Characters)
        {
            var names = new List<string> { c.En };
            names.AddRange(c.Aliases);
            foreach (var raw in names)
            {
                var n = LibraryText.Normalize(raw ?? "");
                if (n.Length == 0) continue;
                _whole.TryAdd(n, c);
                _mentionNames.Add((" " + n + " ", c));
                var parts = n.Split(' ');
                if (parts.Length < 2) continue;
                foreach (var w in parts)
                {
                    if (w.Length < 3) continue;
                    if (!_words.TryGetValue(w, out var owner)) _words[w] = c;
                    else if (owner is not null && !ReferenceEquals(owner, c)) _words[w] = null;
                }
            }
        }
        foreach (var p in info.Places)
        {
            var n = LibraryText.Normalize(p.En ?? "");
            if (n.Length > 0) _placeNames.Add((" " + n + " ", p));
        }
    }

    public GameCharacter? Resolve(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var n = LibraryText.Normalize(Parenthetical.Replace(label, " "));
        if (n.Length == 0) return null;
        if (_whole.TryGetValue(n, out var c)) return c;
        if (_words.TryGetValue(n, out var w)) return w;
        return null;
    }

    public IReadOnlyList<GameCharacter> MentionedIn(string text)
    {
        var padded = " " + LibraryText.Normalize(text) + " ";
        var result = new List<GameCharacter>();
        foreach (var (name, c) in _mentionNames)
            if (padded.Contains(name, StringComparison.Ordinal) && !result.Contains(c)) result.Add(c);
        return result;
    }

    public IReadOnlyList<GamePlace> PlacesIn(string text)
    {
        var padded = " " + LibraryText.Normalize(text) + " ";
        var result = new List<GamePlace>();
        foreach (var (name, p) in _placeNames)
            if (padded.Contains(name, StringComparison.Ordinal) && !result.Contains(p)) result.Add(p);
        return result;
    }
}
