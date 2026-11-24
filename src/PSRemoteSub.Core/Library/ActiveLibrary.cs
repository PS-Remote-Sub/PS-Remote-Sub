namespace PSRemoteSub.Core.Library;

/// <summary>
/// The library in use for the current game: its data, the character index, the matcher (released and learned lines).
/// Thread-safe: the pipeline looks lines up on the tick, the AI lane teaches its answers to the matcher (in memory only).
/// </summary>
public sealed class ActiveLibrary(LibraryData data, CharacterIndex characters, LibraryMatcher matcher)
{
    private int _hits;
    private int _misses;

    public LibraryData Data { get; } = data ?? throw new ArgumentNullException(nameof(data));
    public CharacterIndex Characters { get; } = characters ?? throw new ArgumentNullException(nameof(characters));
    public LibraryMatcher Matcher { get; } = matcher ?? throw new ArgumentNullException(nameof(matcher));

    /// <summary>
    /// Indexes <paramref name="data"/>; the matcher starts with nothing learned. Slow: call it off the UI thread and off the tick.
    /// With <paramref name="indexCache"/> (a file in the user folder) and the sha256 of the lines read, the released
    /// lines' index is loaded from that cache when it matches, and built and written there when it does not.
    /// </summary>
    public static ActiveLibrary Build(LibraryData data, string? indexCache = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var characters = new CharacterIndex(data.Info);
        var index = indexCache is not null && data.LinesSha256 is { } key
            ? LibraryIndex.LoadOrBuild(indexCache, key, data.Lines)
            : LibraryIndex.Build(data.Lines);
        var matcher = new LibraryMatcher(data.Lines, [], characters, index);
        return new ActiveLibrary(data, characters, matcher);
    }

    /// <summary>A line shown from the library without the AI.</summary>
    public void CountHit() => Interlocked.Increment(ref _hits);

    /// <summary>A line looked up in the library that went to the AI.</summary>
    public void CountMiss() => Interlocked.Increment(ref _misses);

    /// <summary>Lookups so far (for the log: counts only, never text).</summary>
    public (int Hits, int Misses) Counts => (Volatile.Read(ref _hits), Volatile.Read(ref _misses));
}
