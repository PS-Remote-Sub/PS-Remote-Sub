using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Tests.Translation;

namespace PSRemoteSub.Core.Tests.Library;

public sealed class LibraryPackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lpack-" + Guid.NewGuid().ToString("N"));
    private string Dir => Path.Combine(_root, "library", "g");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private void Seed(string lines)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, "subtitles.jsonl"), lines);
        File.WriteAllText(Path.Combine(Dir, "manifest.json"), "{\"version\":\"20261101.1\",\"subtitleCount\":1,\"subtitlesSha256\":\"x\",\"tone_sha256\":\"y\"}");
        File.WriteAllText(Path.Combine(Dir, "world.json"), "{\"title\":\"G\"}");
    }

    [Fact]
    public void Append_KeepsEditedSource_ElseAi()
    {
        Seed("{\"en\":\"Known\",\"th\":\"รู้\",\"source\":\"library\"}\n");
        var pending = Path.Combine(Dir, "pending.jsonl");
        File.WriteAllLines(pending,
        [
            "{\"en\":\"Edited\",\"th\":\"แก้แล้ว\",\"source\":\"human\"}",
            "{\"en\":\"Blank source\",\"th\":\"ว่าง\",\"source\":\"\"}",
            "{\"en\":\"No source\",\"th\":\"ไม่มี\"}",
        ]);

        Assert.Equal(3, LibraryPack.Append(Dir, pending));

        var lines = LibraryLoader.ReadLines(Path.Combine(Dir, "subtitles.jsonl"), "library").ToList();
        Assert.Equal(["library", "human", "ai", "ai"], lines.Select(l => l.Source));
    }

    [Fact]
    public void Append_AddsAiSource()
    {
        Seed("{\"en\":\"Known\",\"th\":\"รู้\",\"source\":\"library\"}"); // no trailing newline
        var pending = Path.Combine(Dir, "pending.jsonl");
        File.WriteAllLines(pending,
        [
            "{\"en\":\"Fresh\",\"th\":\"สด\",\"speaker\":\"Jason\",\"model\":\"m\"}",
            "{\"en\":\"known\",\"th\":\"ซ้ำ\"}",
        ]);

        var n = LibraryPack.Append(Dir, pending);

        Assert.Equal(1, n);
        var text = File.ReadAllText(Path.Combine(Dir, "subtitles.jsonl"));
        Assert.EndsWith("\n", text);
        Assert.DoesNotContain("\r", text);
        var lines = LibraryLoader.ReadLines(Path.Combine(Dir, "subtitles.jsonl"), "library").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("Fresh", lines[1].En);
        Assert.Equal("ai", lines[1].Source);
        Assert.Equal("Jason", lines[1].Speaker);
        Assert.Null(lines[1].Scene);
        Assert.DoesNotContain("scene", text.Split('\n')[1]);
    }

    private string DoPack(out string outDir)
    {
        Seed("{\"en\":\"A\",\"th\":\"เอ\",\"source\":\"library\"}\n\n{\"en\":\"B\",\"th\":\"บี\",\"source\":\"library\"}\n");
        File.WriteAllText(Path.Combine(Dir, "translation-guide.md"), "style");
        File.WriteAllText(Path.Combine(Dir, "learned.jsonl"), "should not ship");
        outDir = Path.Combine(_root, "dist");
        return LibraryPack.Pack(Dir, "g", new LibraryVersion(20261121, 1), outDir);
    }

    [Fact]
    public void Pack_WritesVersionAndSha_ZipFlat()
    {
        var zipPath = DoPack(out var outDir);

        Assert.Equal(Path.Combine(outDir, "library-g-20261121.1.zip"), zipPath);
        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
        Assert.Equal(["manifest.json", "subtitles.jsonl", "translation-guide.md", "world.json"], names);

        byte[] Read(string n)
        {
            using var s = zip.GetEntry(n)!.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        using var v = JsonDocument.Parse(Read("manifest.json"));
        Assert.Equal("20261121.1", v.RootElement.GetProperty("version").GetString());
        Assert.Equal(2, v.RootElement.GetProperty("subtitleCount").GetInt32());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Read("subtitles.jsonl"))).ToLowerInvariant(),
            v.RootElement.GetProperty("subtitlesSha256").GetString());
        Assert.Equal(3, v.RootElement.EnumerateObject().Count());
        Assert.False(v.RootElement.TryGetProperty("tone_sha256", out _));
    }

    [Fact]
    public async Task Pack_OutputInstallsWithUpdater()
    {
        var zipPath = DoPack(out _);
        var zip = File.ReadAllBytes(zipPath);
        var releases = "[{\"tag_name\":\"lib-20261121.1\",\"prerelease\":true,\"assets\":[{\"name\":\"library-g-20261121.1.zip\",\"browser_download_url\":\"https://dl.test/g.zip\"}]}]";
        var handler = new StubHttpHandler(req => req.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releases) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
        var user = Path.Combine(_root, "user");
        var updater = new LibraryUpdater(new HttpClient(handler), new LibraryStore(Path.Combine(_root, "bundled"), user), "https://github.com/o/r");

        var res = await updater.CheckAndInstallAsync(CancellationToken.None);

        var l = Assert.Single(res);
        Assert.Equal(new LibraryVersion(20261121, 1), l.Version);
        Assert.True(File.Exists(Path.Combine(user, "g", "subtitles.jsonl")));
    }
}
