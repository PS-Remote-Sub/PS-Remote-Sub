namespace PSRemoteSub.Core.Library;

public sealed record LibraryLine(string En, string Th, string Source, string? Scene, string? Video, string? Speaker, int Order);

public sealed record GlossaryTerm(string En, string Th, string Note);

public sealed record GameCharacter(
    string En, IReadOnlyList<string> Aliases, string? Gender, string? ThName, string? ThCall,
    string? Self, string? Group, string? Note);

public sealed record GamePlace(string En, string? Th, string? Note);

public sealed record GameInfo(
    string? Title, string? Setting, IReadOnlyList<GameCharacter> Characters, IReadOnlyList<GamePlace> Places)
{
    public static GameInfo Empty { get; } = new(null, null, [], []);
}

/// <param name="Sha256">The sha256 manifest.json states for subtitles.jsonl.</param>
/// <param name="LinesSha256">The sha256 of the subtitles.jsonl bytes actually read (keys the index cache), when loaded from a file.</param>
public sealed record LibraryData(
    string Game, LibraryVersion Version, string? Sha256, IReadOnlyList<LibraryLine> Lines,
    IReadOnlyList<GlossaryTerm> Glossary, GameInfo Info, string Style, string? LinesSha256 = null);
