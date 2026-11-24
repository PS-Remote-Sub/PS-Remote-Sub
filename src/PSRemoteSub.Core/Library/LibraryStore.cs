using System.Text.Json;

namespace PSRemoteSub.Core.Library;

public sealed record LibraryListing(string Game, string? Title, LibraryVersion Version, string Dir);

/// <summary>Finds libraries: a baseline shipped next to the exe and newer ones downloaded into the user folder.</summary>
public sealed class LibraryStore(string bundledRoot, string userRoot)
{
    public string UserRoot => userRoot;

    public string UserDir(string game) => Path.Combine(userRoot, game);

    /// <summary>The game's index cache: always in the user folder (the bundled folder may be read-only).</summary>
    public string IndexPath(string game) => Path.Combine(userRoot, game, "index.bin");

    /// <summary>
    /// Loads the game's library and builds it for use (the index from its cache when that matches). Null when the
    /// library is missing or unreadable. Slow: call it off the UI thread and off the tick.
    /// </summary>
    public ActiveLibrary? Build(string game)
    {
        if (DirFor(game) is not { } dir || LibraryLoader.Load(dir, game) is not { } data)
            return null;
        return ActiveLibrary.Build(data, IndexPath(game));
    }

    /// <summary>
    /// A library folder name that cannot leave the library root (settings may be edited by hand): trimmed, then
    /// non-empty, no path or invalid characters (a backslash is rejected on every OS), and not only dots.
    /// </summary>
    public static bool IsGameName(string? game)
    {
        var name = game?.Trim();
        return !string.IsNullOrEmpty(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !name.Contains('\\')
            && name.Trim('.').Length > 0;
    }

    /// <summary>Whether any profile chose <paramref name="game"/>'s library (<paramref name="libraryGames"/>: each
    /// profile's library setting).</summary>
    public static bool IsInUse(IEnumerable<string?> libraryGames, string game) =>
        libraryGames.Any(g => IsGameName(g) && string.Equals(g!.Trim(), game, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<LibraryListing> List()
    {
        var games = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { bundledRoot, userRoot })
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var d in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(d);
                    if (!name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                        && !name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) games.Add(name);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var result = new List<LibraryListing>();
        foreach (var game in games)
        {
            var best = Pick(game);
            if (best is { } b) result.Add(new LibraryListing(game, ReadTitle(b.Dir), b.Version, b.Dir));
        }
        return result;
    }

    public string? DirFor(string game) => Pick(game)?.Dir;

    public LibraryVersion? VersionOf(string game) => Pick(game)?.Version;

    private (string Dir, LibraryVersion Version)? Pick(string game)
    {
        var b = Path.Combine(bundledRoot, game);
        var u = Path.Combine(userRoot, game);
        var bv = LibraryLoader.ReadVersion(b);
        var uv = LibraryLoader.ReadVersion(u);
        if (bv is null && uv is null) return null;
        if (uv is null) return (b, bv!.Value);
        if (bv is null) return (u, uv.Value);
        return uv.Value > bv.Value ? (u, uv.Value) : (b, bv.Value);
    }

    private static string? ReadTitle(string dir)
    {
        try
        {
            var path = Path.Combine(dir, LibraryFiles.World);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
