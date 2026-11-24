using PSRemoteSub.Core.Library;

namespace PSRemoteSub.Core.Tests.Library;

public sealed class LibraryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lstore-" + Guid.NewGuid().ToString("N"));
    private string Bundled => Path.Combine(_root, "bundled");
    private string User => Path.Combine(_root, "user");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static void Make(string root, string game, string version, string? title = null)
    {
        var dir = Path.Combine(root, game);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), $"{{\"version\":\"{version}\",\"subtitleCount\":0,\"subtitlesSha256\":\"x\"}}");
        File.WriteAllText(Path.Combine(dir, "subtitles.jsonl"), "");
        if (title is not null) File.WriteAllText(Path.Combine(dir, "world.json"), $"{{\"title\":\"{title}\"}}");
    }

    [Theory]
    [InlineData("gta6", true)]
    [InlineData(" gta6 ", true)]
    [InlineData("my.game", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData(" .. ", false)]
    [InlineData(" . ", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("..\\x", false)]
    public void IsGameName_TrimsThenValidates(string? game, bool expected) =>
        Assert.Equal(expected, LibraryStore.IsGameName(game));

    [Fact]
    public void IsInUse_OnlyWhenAProfileChoseTheGame()
    {
        Assert.True(LibraryStore.IsInUse([null, " GTA6 "], "gta6"));
        Assert.False(LibraryStore.IsInUse([null, ""], "gta6")); // a player who uses no library
        Assert.False(LibraryStore.IsInUse([], "gta6"));
        Assert.False(LibraryStore.IsInUse(["rdr2"], "gta6"));
        Assert.False(LibraryStore.IsInUse([" .. "], ".."));
    }

    [Fact]
    public void List_PicksNewerOfBundledAndUser()
    {
        Make(Bundled, "gta6", "20261101.1", "GTA VI");
        Make(User, "gta6", "20261120.2", "GTA VI");
        Make(Bundled, "rdr", "20261101.1");
        Make(User, "rdr", "20261101.1");

        var store = new LibraryStore(Bundled, User);
        var list = store.List();

        var gta = Assert.Single(list, l => l.Game == "gta6");
        Assert.Equal(new LibraryVersion(20261120, 2), gta.Version);
        Assert.Equal(Path.Combine(User, "gta6"), gta.Dir);
        Assert.Equal("GTA VI", gta.Title);
        Assert.Equal(Path.Combine(Bundled, "rdr"), store.DirFor("rdr"));
    }

    [Fact]
    public void List_UserOnlyGameAppears()
    {
        Make(User, "newgame", "20261120.1", "New");
        Directory.CreateDirectory(Path.Combine(Bundled, "broken"));

        var store = new LibraryStore(Bundled, User);
        var l = Assert.Single(store.List());
        Assert.Equal("newgame", l.Game);
        Assert.Null(store.DirFor("broken"));
        Assert.Null(store.DirFor("missing"));
    }
}
