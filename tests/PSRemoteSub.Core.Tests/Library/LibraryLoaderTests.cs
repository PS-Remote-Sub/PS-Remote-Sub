using PSRemoteSub.Core.Library;

namespace PSRemoteSub.Core.Tests.Library;

public sealed class LibraryLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "psrs-lib-" + Guid.NewGuid().ToString("N"));

    public LibraryLoaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(_dir, name), content);

    private void WriteVersion() =>
        Write("manifest.json", "{\"version\":\"20261120.12\",\"subtitleCount\":2,\"subtitlesSha256\":\"abc\"}");

    [Fact]
    public void Load_ReadsAllFiles()
    {
        WriteVersion();
        Write("subtitles.jsonl",
            "{\"en\":\"Hello\",\"th\":\"สวัสดี\",\"source\":\"claude\",\"scene\":\"s1\",\"video\":\"v1\",\"speaker\":\"Lucia\"}\n" +
            "{\"en\":\"Bye\",\"th\":\"ลาก่อน\"}\n");
        Write("terms.json", "[{\"en\":\"grand\",\"th\":\"พัน\",\"notes\":\"slang\"}]");
        Write("world.json",
            "{\"title\":\"T\",\"setting\":\"S\",\"characters\":[{\"en\":\"Lucia\",\"alsoCalled\":[\"Lu\",5,\"Lou\"],\"gender\":\"female\",\"th\":\"ลูเซีย\",\"thShort\":\"ลู\",\"speaksAs\":\"ฉัน\",\"role\":\"g\",\"notes\":\"n\"}],\"places\":[{\"en\":\"Vice City\",\"th\":\"ไวซ์ซิตี้\",\"notes\":\"pn\"}],\"style_file\":\"translation-guide.md\"}");
        Write("translation-guide.md", "be nice");

        var d = LibraryLoader.Load(_dir, "gta6");

        Assert.NotNull(d);
        Assert.Equal("gta6", d.Game);
        Assert.Equal("20261120.12", d.Version.ToString());
        Assert.Equal("abc", d.Sha256);
        Assert.Equal(2, d.Lines.Count);
        Assert.Equal(0, d.Lines[0].Order);
        Assert.Equal(1, d.Lines[1].Order);
        Assert.Equal("Hello", d.Lines[0].En);
        Assert.Equal("claude", d.Lines[0].Source);
        Assert.Equal("s1", d.Lines[0].Scene);
        Assert.Equal("v1", d.Lines[0].Video);
        Assert.Equal("Lucia", d.Lines[0].Speaker);
        Assert.Null(d.Lines[1].Scene);
        Assert.Single(d.Glossary);
        Assert.Equal("slang", d.Glossary[0].Note);
        var c = Assert.Single(d.Info.Characters);
        Assert.Equal("Lucia", c.En);
        Assert.Equal(new[] { "Lu", "Lou" }, c.Aliases);
        Assert.Equal("ลู", c.ThCall);
        Assert.Equal("T", d.Info.Title);
        var p = Assert.Single(d.Info.Places);
        Assert.Equal("ไวซ์ซิตี้", p.Th);
        Assert.Equal("be nice", d.Style);
    }

    [Fact]
    public void Load_LinesSha256_IsTheHashOfTheLinesFile()
    {
        WriteVersion();
        var bytes = "﻿{\"en\":\"Hello\",\"th\":\"สวัสดี\"}\r\n\r\nnot json\n{\"en\":\"Bye\",\"th\":\"ลาก่อน\"}"u8.ToArray();
        File.WriteAllBytes(Path.Combine(_dir, "subtitles.jsonl"), bytes);

        var d = LibraryLoader.Load(_dir, "gta6");

        Assert.NotNull(d);
        Assert.Equal(2, d.Lines.Count);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), d.LinesSha256);
        Assert.Equal("abc", d.Sha256); // manifest.json's value is kept as it was
    }

    [Fact]
    public void Load_SkipsBadLines()
    {
        WriteVersion();
        Write("subtitles.jsonl",
            "{\"en\":\"a\",\"th\":\"ก\"}\n{oops\n{\"en\":\"x\"}\n{\"en\":\"b\",\"th\":\"ข\"}\n");
        var d = LibraryLoader.Load(_dir, "g");
        Assert.NotNull(d);
        Assert.Equal(2, d.Lines.Count);
        Assert.Equal(1, d.Lines[1].Order);
    }

    [Fact]
    public void Load_MissingVersion_ReturnsNull()
    {
        Write("subtitles.jsonl", "{\"en\":\"a\",\"th\":\"ก\"}\n");
        Assert.Null(LibraryLoader.Load(_dir, "g"));
    }

    [Fact]
    public void Load_MissingOptionalFiles_GivesEmpty()
    {
        WriteVersion();
        Write("subtitles.jsonl", "{\"en\":\"a\",\"th\":\"ก\"}\n");
        var d = LibraryLoader.Load(_dir, "g");
        Assert.NotNull(d);
        Assert.Empty(d.Glossary);
        Assert.Empty(d.Info.Characters);
        Assert.Empty(d.Info.Places);
        Assert.Equal("", d.Style);
    }

    [Fact]
    public void Version_ComparesDateThenN()
    {
        Assert.True(LibraryVersion.TryParse("20261120.12", out var a));
        Assert.True(LibraryVersion.TryParse("20261120.9", out var b));
        Assert.True(LibraryVersion.TryParse("20261119.99", out var c));
        Assert.True(a.CompareTo(b) > 0);
        Assert.True(b.CompareTo(c) > 0);
        Assert.False(LibraryVersion.TryParse("abc", out _));
    }

    [Fact]
    public void Load_RealGta6()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "PSRemoteSub.sln")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);

        var d = LibraryLoader.Load(Path.Combine(root, "library", "gta6"), "gta6");

        Assert.NotNull(d);
        // Lines and the version grow with each library release (a lib-* tag must not need a test edit).
        Assert.True(d.Lines.Count >= 25619, $"lines: {d.Lines.Count}");
        Assert.Equal(59, d.Glossary.Count);
        Assert.Equal(18, d.Info.Characters.Count);
        Assert.Equal(13, d.Info.Places.Count);
        Assert.True(d.Version >= new LibraryVersion(20261120, 12), $"version: {d.Version}");
    }
}
