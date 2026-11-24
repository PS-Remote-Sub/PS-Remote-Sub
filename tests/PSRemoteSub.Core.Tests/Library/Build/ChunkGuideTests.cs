using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Library.Build;

namespace PSRemoteSub.Core.Tests.Library.Build;

public sealed class ChunkGuideTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lguide-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static GameCharacter Char(string en, string th, string note) =>
        new(en, [], "male", th, th, "ฉัน", "group", note);

    [Fact]
    public void Render_includes_jason_lucia_and_mentioned_people_but_never_thai_names()
    {
        var info = new GameInfo("GTA", "Leonida",
            [Char("Jason", "เจสัน", "J"), Char("Lucia", "ลูเซีย", "L"), Char("Cal", "แคล", new string('x', 500)), Char("Boobie", "บูบี้", "B")],
            [new GamePlace("Vice City", "ไวซ์ซิตี้", "city")]);
        var chunk = new Chunk(1, [new Segment("s000001", "Cal, meet me in Vice City.", "m@1", "Main story (story000)", null)]);
        var g = ChunkGuide.Render(chunk, [new GlossaryTerm("meet", "เจอ", "")], info, "STYLE");
        Assert.Contains("STYLE", g);
        Assert.Contains("Main story (story000)", g);
        Assert.Contains("Jason", g);
        Assert.Contains("Lucia", g);
        Assert.Contains("Cal", g);
        Assert.DoesNotContain("Boobie", g);
        Assert.DoesNotContain("แคล", g);
        Assert.DoesNotContain("เจสัน", g);
        Assert.DoesNotContain(new string('x', 401), g);
        Assert.Contains("meet → เจอ", g);
        Assert.Contains("Vice City: ไวซ์ซิตี้ — city", g);
    }

    [Fact]
    public void LoadGuide_reads_guide_files_without_version_or_lines()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "terms.json"), """[{"en":"cop","th":"ตำรวจ","notes":""}]""");
        File.WriteAllText(Path.Combine(_dir, "world.json"), """{"title":"T","characters":[{"en":"Jason","alsoCalled":["Jay"]}],"places":[]}""");
        File.WriteAllText(Path.Combine(_dir, "translation-guide.md"), "S");
        var (glossary, info, style) = LibraryLoader.LoadGuide(_dir, "g");
        Assert.Equal("ตำรวจ", Assert.Single(glossary).Th);
        Assert.Equal("Jason", Assert.Single(info.Characters).En);
        Assert.Equal("S", style);
    }
}
