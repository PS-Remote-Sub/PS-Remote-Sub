using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Tests.Translation;

namespace PSRemoteSub.Core.Tests.Library;

public sealed class LibraryUpdaterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lupd-" + Guid.NewGuid().ToString("N"));
    private string Bundled => Path.Combine(_root, "bundled");
    private string User => Path.Combine(_root, "user");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static byte[] Zip(string version, string? lines, string? sha = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
            Add("manifest.json", $"{{\"version\":\"{version}\",\"subtitleCount\":1,\"subtitlesSha256\":\"{sha ?? Sha(lines ?? "")}\"}}");
            if (lines is not null) Add("subtitles.jsonl", lines);
            Add("world.json", "{\"title\":\"G\"}");
            Add("../evil.txt", "x");
            Add("sub/subtitles.jsonl", "BAD");
        }
        return ms.ToArray();
    }

    private void Seed(string game, string version, string lines)
    {
        var dir = Path.Combine(User, game);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), $"{{\"version\":\"{version}\",\"subtitleCount\":1,\"subtitlesSha256\":\"{Sha(lines)}\"}}");
        File.WriteAllText(Path.Combine(dir, "subtitles.jsonl"), lines);
        File.WriteAllText(Path.Combine(dir, "learned.jsonl"), "learned");
    }

    private static string Releases(string tag, string game, string version) =>
        $"[{{\"tag_name\":\"{tag}\",\"prerelease\":true,\"assets\":[{{\"name\":\"library-{game}-{version}.zip\",\"browser_download_url\":\"https://dl.test/{game}-{version}.zip\"}}]}}]";

    private LibraryUpdater Updater(string releasesJson, byte[] zip, out StubHttpHandler handler)
    {
        handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.Host == "api.github.com")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releasesJson) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
        });
        return new LibraryUpdater(new HttpClient(handler), new LibraryStore(Bundled, User), "https://github.com/o/r");
    }

    [Fact]
    public async Task Install_NewerRelease_ReplacesFilesKeepsLearned()
    {
        Seed("g", "20261101.1", "old");
        var u = Updater(Releases("lib-20261120.2", "g", "20261120.2"), Zip("20261120.2", "new"), out var h);

        var res = await u.CheckAndInstallAsync(CancellationToken.None);

        var l = Assert.Single(res);
        Assert.Equal("g", l.Game);
        Assert.Equal(new LibraryVersion(20261120, 2), l.Version);
        Assert.Equal("new", File.ReadAllText(Path.Combine(User, "g", "subtitles.jsonl")));
        Assert.Equal("learned", File.ReadAllText(Path.Combine(User, "g", "learned.jsonl")));
        Assert.True(File.Exists(Path.Combine(User, "g", "world.json")));
        Assert.False(Directory.Exists(Path.Combine(User, "g.tmp")));
        Assert.False(File.Exists(Path.Combine(User, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(User, "g", "evil.txt")));
        Assert.False(Directory.Exists(Path.Combine(User, "g", "sub")));
        Assert.StartsWith("https://api.github.com/repos/o/r/releases", h.Requests[0].ToString());
    }

    [Fact]
    public async Task Install_RemovesFilesOfTheOldFormat()
    {
        var dir = Path.Combine(User, "g");
        Directory.CreateDirectory(dir);
        string[] old = ["version.json", "lines.jsonl", "glossary.json", "game.json", "style.md"];
        foreach (var f in old) File.WriteAllText(Path.Combine(dir, f), "old");
        File.WriteAllText(Path.Combine(dir, "learned.jsonl"), "learned");
        var u = Updater(Releases("lib-20261120.2", "g", "20261120.2"), Zip("20261120.2", "new"), out _);

        Assert.Single(await u.CheckAndInstallAsync(CancellationToken.None));

        Assert.All(old, f => Assert.False(File.Exists(Path.Combine(dir, f)), f));
        Assert.Equal("new", File.ReadAllText(Path.Combine(dir, "subtitles.jsonl")));
        Assert.Equal("learned", File.ReadAllText(Path.Combine(dir, "learned.jsonl")));
    }

    [Fact]
    public async Task Install_OlderRelease_Ignored()
    {
        Seed("g", "20261120.5", "old");
        var u = Updater(Releases("lib-20261120.2", "g", "20261120.2"), Zip("20261120.2", "new"), out _);
        Assert.Empty(await u.CheckAndInstallAsync(CancellationToken.None));
        Assert.Equal("old", File.ReadAllText(Path.Combine(User, "g", "subtitles.jsonl")));
    }

    [Fact]
    public async Task Install_ShaMismatch_KeepsOldLibrary()
    {
        Seed("g", "20261101.1", "old");
        var u = Updater(Releases("lib-20261120.2", "g", "20261120.2"), Zip("20261120.2", "new", sha: "deadbeef"), out _);
        Assert.Empty(await u.CheckAndInstallAsync(CancellationToken.None));
        Assert.Equal("old", File.ReadAllText(Path.Combine(User, "g", "subtitles.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(User, "g.tmp")));
    }

    [Fact]
    public async Task Install_ZipWithoutLines_KeepsOld()
    {
        Seed("g", "20261101.1", "old");
        var u = Updater(Releases("lib-20261120.2", "g", "20261120.2"), Zip("20261120.2", null), out _);
        Assert.Empty(await u.CheckAndInstallAsync(CancellationToken.None));
        Assert.Equal("old", File.ReadAllText(Path.Combine(User, "g", "subtitles.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(User, "g.tmp")));
    }

    [Fact]
    public async Task Check_ApiError_ReturnsEmpty()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var u = new LibraryUpdater(new HttpClient(handler), new LibraryStore(Bundled, User), "https://github.com/o/r");
        Assert.Empty(await u.CheckAndInstallAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Check_IgnoresAppReleases()
    {
        var u = Updater(Releases("v1.2.0", "g", "20261120.2"), Zip("20261120.2", "new"), out var h);
        Assert.Empty(await u.CheckAndInstallAsync(CancellationToken.None));
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Check_EmptyRepository_DoesNothing()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var u = new LibraryUpdater(new HttpClient(handler), new LibraryStore(Bundled, User), "");
        Assert.Empty(await u.CheckAndInstallAsync(CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Install_OptionalFileAbsentFromZip_IsRemoved()
    {
        Seed("g", "20261101.1", "old");
        File.WriteAllText(Path.Combine(User, "g", "translation-guide.md"), "style");
        var u = Updater(Releases("lib-20261120.2", "g", "20261120.2"), Zip("20261120.2", "new"), out _);

        Assert.Single(await u.CheckAndInstallAsync(CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(User, "g", "translation-guide.md")));
        Assert.Equal("learned", File.ReadAllText(Path.Combine(User, "g", "learned.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(User, "g.bak")));
    }

    [Fact]
    public async Task Install_FailureMidMove_RestoresOldFiles()
    {
        Seed("g", "20261101.1", "old");
        File.WriteAllText(Path.Combine(User, "g", "translation-guide.md"), "style");
        var u = Updater(Releases("lib-20261120.2", "g", "20261120.2"), Zip("20261120.2", "new"), out _);
        u.BeforeMove = f => { if (f == "manifest.json") throw new IOException("locked"); };

        Assert.Empty(await u.CheckAndInstallAsync(CancellationToken.None));

        var dir = Path.Combine(User, "g");
        Assert.Equal("old", File.ReadAllText(Path.Combine(dir, "subtitles.jsonl")));
        Assert.Equal("style", File.ReadAllText(Path.Combine(dir, "translation-guide.md")));
        Assert.Equal(new LibraryVersion(20261101, 1), LibraryLoader.ReadVersion(dir));
        Assert.False(File.Exists(Path.Combine(dir, "world.json")));
        Assert.Equal("learned", File.ReadAllText(Path.Combine(dir, "learned.jsonl")));
        Assert.False(Directory.Exists(dir + ".bak"));
        Assert.False(Directory.Exists(dir + ".tmp"));
    }

    [Fact]
    public async Task Install_RestoreFails_KeepsBackup()
    {
        Seed("g", "20261101.1", "old");
        var u = Updater(Releases("lib-20261120.2", "g", "20261120.2"), Zip("20261120.2", "new"), out _);
        u.BeforeMove = f => { if (f == "manifest.json") throw new IOException("locked"); };
        u.BeforeRestore = f => { if (f == "subtitles.jsonl") throw new IOException("locked"); };

        Assert.Empty(await u.CheckAndInstallAsync(CancellationToken.None));

        var bak = Path.Combine(User, "g.bak");
        Assert.Equal("old", File.ReadAllText(Path.Combine(bak, "subtitles.jsonl")));
        Assert.Equal("learned", File.ReadAllText(Path.Combine(User, "g", "learned.jsonl")));
    }

    [Fact]
    public async Task Check_Cancelled_ReturnsEmpty()
    {
        var u = Updater("[]", [], out _);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Empty(await u.CheckAndInstallAsync(cts.Token));
    }
}
