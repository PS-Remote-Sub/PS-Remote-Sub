using PSRemoteSub.Core.Library;

namespace PSRemoteSub.Core.Tests.Library;

public sealed class LibraryIndexCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lindex-" + Guid.NewGuid().ToString("N"));
    private string Bundled => Path.Combine(_root, "bundled");
    private string User => Path.Combine(_root, "user");
    private string CachePath => Path.Combine(_root, "cache", "index.bin");

    public LibraryIndexCacheTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static LibraryLine V(string en, string th, int order, string? video = "v1", string? scene = "s") =>
        new(en, th, "claude", scene, video, null, order);

    private static readonly LibraryLine[] Sample =
    [
        V("We owe that bitch of a landlady her rent, so I'm gonna stay out here a while,", "ไทย1", 0),
        V("and try to get a few grand stacked away.", "ไทย2", 1),
        V("I met this guy who can help with cars.", "ไทย3", 2),
        V("I need 50 grand by tonight", "ไทย4", 3, null, null),
    ];

    private static LibraryMatcher Matcher(IReadOnlyList<LibraryLine> lines, LibraryIndex index) =>
        new(lines, [], CharacterIndexTests.Sample(), index);

    private static void AssertSampleWorks(LibraryMatcher m)
    {
        Assert.Equal("ไทย2", m.Lookup("and try to get a few grond stacked away", null)?.Thai);
        Assert.Equal("ไทย1 ไทย2", m.Lookup(Sample[0].En + " " + Sample[1].En, null)?.Thai);
        Assert.Null(m.Lookup("I need 60 grand by tonight", null));
    }

    [Fact]
    public void RoundTrip_LoadedIndexKeepsEveryGoldenResult()
    {
        var (data, golden) = LibraryGoldenTests.Load();
        var built = LibraryIndex.LoadOrBuild(CachePath, golden.Sha, data.Lines);
        Assert.True(File.Exists(CachePath));
        var loaded = LibraryIndex.TryLoad(CachePath, golden.Sha, data.Lines);
        Assert.NotNull(loaded);
        Assert.NotSame(built, loaded);

        var characters = new CharacterIndex(data.Info);
        var bad = LibraryGoldenTests.Diff(golden,
            mode => new LibraryMatcher(data.Lines, mode == "l" ? golden.Learned : [], characters, loaded));
        Assert.True(bad.Count == 0, $"{bad.Count} golden results changed:\n" + string.Join("\n", bad.Take(30)));
    }

    [Fact]
    public void LoadOrBuild_WritesThenLoads()
    {
        LibraryIndex.LoadOrBuild(CachePath, "key", Sample);
        var written = File.GetLastWriteTimeUtc(CachePath);
        File.SetLastWriteTimeUtc(CachePath, written.AddMinutes(-5));

        var again = LibraryIndex.LoadOrBuild(CachePath, "key", Sample);

        Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(CachePath)); // loaded, not rewritten
        AssertSampleWorks(Matcher(Sample, again));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(CachePath)!, "*.tmp"));
    }

    [Fact]
    public void StaleKey_Rebuilds()
    {
        LibraryIndex.LoadOrBuild(CachePath, "old", Sample);
        Assert.Null(LibraryIndex.TryLoad(CachePath, "new", Sample));

        var index = LibraryIndex.LoadOrBuild(CachePath, "new", Sample);

        AssertSampleWorks(Matcher(Sample, index));
        Assert.NotNull(LibraryIndex.TryLoad(CachePath, "new", Sample)); // rewritten for the new key
        Assert.Null(LibraryIndex.TryLoad(CachePath, "old", Sample));
    }

    [Fact]
    public void OtherLines_SameKey_NotLoaded()
    {
        LibraryIndex.LoadOrBuild(CachePath, "key", Sample);
        Assert.Null(LibraryIndex.TryLoad(CachePath, "key", Sample[..3]));
        var changed = Sample.Select(l => l with { En = l.En + " again" }).ToArray();
        Assert.Null(LibraryIndex.TryLoad(CachePath, "key", changed));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("truncated")]
    [InlineData("flipped")]
    [InlineData("empty")]
    public void Corrupt_RebuildsAndDoesNotThrow(string how)
    {
        LibraryIndex.LoadOrBuild(CachePath, "key", Sample);
        var bytes = File.ReadAllBytes(CachePath);
        switch (how)
        {
            case "garbage":
                new Random(1).NextBytes(bytes);
                break;
            case "truncated":
                bytes = bytes[..(bytes.Length / 2)];
                break;
            case "flipped":
                bytes[bytes.Length * 2 / 3] ^= 0x10;
                break;
            case "empty":
                bytes = [];
                break;
        }
        File.WriteAllBytes(CachePath, bytes);

        Assert.Null(LibraryIndex.TryLoad(CachePath, "key", Sample));
        var index = LibraryIndex.LoadOrBuild(CachePath, "key", Sample);

        AssertSampleWorks(Matcher(Sample, index));
        Assert.NotNull(LibraryIndex.TryLoad(CachePath, "key", Sample));
    }

    [Fact]
    public void MissingOrLockedFolder_StillBuilds()
    {
        Assert.Null(LibraryIndex.TryLoad(Path.Combine(_root, "nope", "index.bin"), "key", Sample));
        // A cache path that cannot be written (its folder is a file): the index is built and the error swallowed.
        File.WriteAllText(Path.Combine(_root, "file"), "");
        var index = LibraryIndex.LoadOrBuild(Path.Combine(_root, "file", "index.bin"), "key", Sample);
        AssertSampleWorks(Matcher(Sample, index));
    }

    private void MakeLibrary(string root, string game)
    {
        var dir = Path.Combine(root, game);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{\"version\":\"20261121.1\",\"subtitleCount\":4,\"subtitlesSha256\":\"x\"}");
        File.WriteAllLines(Path.Combine(dir, "subtitles.jsonl"), Sample.Select(l => System.Text.Json.JsonSerializer.Serialize(
            new { en = l.En, th = l.Th, scene = l.Scene, video = l.Video })));
    }

    private static Dictionary<string, DateTime> Snapshot(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc);

    [Fact]
    public void Store_CachesInTheUserFolder_NeverInTheBundledOne()
    {
        MakeLibrary(Bundled, "gta6");
        var before = Snapshot(Bundled);
        var store = new LibraryStore(Bundled, User);

        var lib = store.Build("gta6");

        Assert.NotNull(lib);
        AssertSampleWorks(lib.Matcher);
        Assert.Equal(before, Snapshot(Bundled));
        Assert.True(File.Exists(store.IndexPath("gta6")));
        Assert.StartsWith(Path.GetFullPath(User), Path.GetFullPath(store.IndexPath("gta6")));

        // Second start: the cache is loaded, not rewritten.
        var written = File.GetLastWriteTimeUtc(store.IndexPath("gta6"));
        File.SetLastWriteTimeUtc(store.IndexPath("gta6"), written.AddMinutes(-5));
        var again = store.Build("gta6");
        Assert.NotNull(again);
        AssertSampleWorks(again.Matcher);
        Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(store.IndexPath("gta6")));
        Assert.Equal(before, Snapshot(Bundled));
    }

    [Fact]
    public void Store_LinesChanged_Rebuilds()
    {
        MakeLibrary(User, "gta6");
        var store = new LibraryStore(Bundled, User);
        Assert.NotNull(store.Build("gta6"));

        var lines = Path.Combine(User, "gta6", "subtitles.jsonl");
        File.AppendAllText(lines, "{\"en\":\"Where is the money\",\"th\":\"เงินอยู่ไหน\"}\n");
        var lib = store.Build("gta6");

        Assert.NotNull(lib);
        Assert.Equal("เงินอยู่ไหน", lib.Matcher.Lookup("where is the monay", null)?.Thai);
    }

    [Fact]
    public void Store_MissingLibrary_Null() =>
        Assert.Null(new LibraryStore(Bundled, User).Build("gta6"));
}
