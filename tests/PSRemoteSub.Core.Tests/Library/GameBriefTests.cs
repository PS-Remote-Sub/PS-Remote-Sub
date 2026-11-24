using PSRemoteSub.Core.Library;

namespace PSRemoteSub.Core.Tests.Library;

public sealed class GameBriefTests
{
    private static GameCharacter Ch(string en, string? note = null, string[]? aliases = null) =>
        new(en, aliases ?? [], "female", "ลูเซีย", "ลู", "ฉัน", null, note);

    private static LibraryData Lib(GameInfo? info = null, string style = "STYLE-HEAD\nline two", params GlossaryTerm[] glossary) =>
        new("t", new LibraryVersion(20260101, 1), null, [],
            glossary.Length > 0 ? glossary : [new("grand", "พัน (ดอลลาร์)", ""), new("ride", "รถ", "")],
            info ?? GameInfo.Empty, style);

    private static GameBrief Build(LibraryData lib, string text, string?[] speakers, int budget = 6000) =>
        GameBriefBuilder.Build(lib, new CharacterIndex(lib.Info), text, speakers, budget);

    [Fact]
    public void Brief_OnlyRelevantTerms()
    {
        var b = Build(Lib(), "a few grand", []);
        Assert.Contains("grand → พัน (ดอลลาร์)", b.PerLine);
        Assert.DoesNotContain("ride", b.PerLine);
    }

    [Fact]
    public void Brief_SpeakerAndMentioned()
    {
        var info = new GameInfo("T", "Setting", [Ch("Lucia", aliases: ["Lu"]), Ch("Jason Duval", aliases: ["Jay"]), Ch("Cal Hampton")], []);
        var b = Build(Lib(info), "Where is Lucia?", ["Jay"]);
        Assert.Contains("Jason Duval:", b.PerLine);
        Assert.Contains("Lucia:", b.PerLine);
        Assert.DoesNotContain("Cal Hampton", b.PerLine);
    }

    [Fact]
    public void Brief_SpeakerAlsoMentioned_AppearsOnce()
    {
        var info = new GameInfo(null, null, [Ch("Lucia", aliases: ["Lu"])], []);
        var b = Build(Lib(info), "Lucia, it's me", ["Lucia"]);
        Assert.Equal(1, b.PerLine.Split("Lucia:").Length - 1);
    }

    [Fact]
    public void Brief_NoteCutAt400()
    {
        var info = new GameInfo(null, null, [Ch("Lucia", note: new string('x', 900))], []);
        var b = Build(Lib(info), "hi Lucia", []);
        Assert.Contains(new string('x', GameBriefBuilder.NoteMax), b.PerLine);
        Assert.DoesNotContain(new string('x', GameBriefBuilder.NoteMax + 1), b.PerLine);
    }

    [Fact]
    public void Brief_StaticHasSettingAndStyle()
    {
        var info = new GameInfo("T", "Vice City, 2020s", [], []);
        var b = Build(Lib(info), "hi", []);
        Assert.Contains("STYLE-HEAD", b.Static);
        Assert.Contains("Vice City, 2020s", b.Static);
    }

    [Fact]
    public void Brief_RespectsBudgetAndPriority()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "PSRemoteSub.sln")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var lib = LibraryLoader.Load(Path.Combine(root, "library", "gta6"), "gta6")!;
        Assert.NotNull(lib);

        var b = GameBriefBuilder.Build(lib, new CharacterIndex(lib.Info), "a few grand for the ride", [], GameBriefBuilder.OllamaBudget);
        Assert.True(b.Static.Length + b.PerLine.Length <= GameBriefBuilder.OllamaBudget);
        Assert.Contains("grand → พันเหรียญ", b.PerLine);
        Assert.True(b.Static.Length < lib.Style.Length, "style should be cut");
        Assert.NotEmpty(b.Static);
    }

    [Fact]
    public void Brief_StyleCutAtLineBreak_SettingDroppedFirst()
    {
        var style = string.Join('\n', Enumerable.Range(0, 50).Select(i => $"style line number {i:00}"));
        var info = new GameInfo("T", "SETTING-TEXT", [], []);
        var b = Build(Lib(info, style), "hi", [], 200);
        Assert.True(b.Static.Length + b.PerLine.Length <= 200);
        Assert.DoesNotContain("SETTING-TEXT", b.Static);
        Assert.StartsWith("style line number 00", b.Static);
        Assert.All(b.Static.Split('\n'), l => Assert.StartsWith("style line number", l));
    }

    [Theory]
    [InlineData(6000)]
    [InlineData(2000)]
    public void Brief_StaticIsSameForEveryLine(int budget)
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "PSRemoteSub.sln")))
            root = Path.GetDirectoryName(root);
        var lib = LibraryLoader.Load(Path.Combine(root!, "library", "gta6"), "gta6")!;
        var chars = new CharacterIndex(lib.Info);
        var a = GameBriefBuilder.Build(lib, chars, "a few grand for the ride, Lucia, in Vice City", ["Jason"], budget);
        var b = GameBriefBuilder.Build(lib, chars, "hello there", [], budget);
        Assert.NotEmpty(a.PerLine);
        Assert.Equal(a.Static, b.Static);
        Assert.True(a.Static.Length + a.PerLine.Length <= budget);
    }
}
