using PSRemoteSub.Core.Library.Build;

namespace PSRemoteSub.Core.Tests.Library.Build;

public sealed class BuildWorkSubmitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lsubmit-" + Guid.NewGuid().ToString("N"));
    private string Work => Path.Combine(_root, "work");
    private string Lib => Path.Combine(_root, "lib");

    public BuildWorkSubmitTests()
    {
        Directory.CreateDirectory(Lib);
        File.WriteAllText(Path.Combine(Lib, "translation-guide.md"), "STYLE-RULES");
        File.WriteAllText(Path.Combine(Lib, "world.json"), """{"characters":[{"en":"Jason"},{"en":"Lucia"}]}""");
        var csv = Path.Combine(_root, "d.csv");
        File.WriteAllText(csv, "part,scene,scene_id,line,speaker,text,raw_text,hash\n" +
            "Main story (story000),1,m@1,1,Jason,\"~sl:0~Hey, you.~sl:1~Move it\",x,AA\n" +
            "Open world / side content (data.rpf),2,w@1,1,Unknown,In Spanish: Hola amigo.,x,CC\n");
        BuildWork.Import(csv, Work);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Draft(int n) => BuildWork.DraftPath(Work, n);
    private string Out(int n) => BuildWork.OutPath(Work, n);

    private string GoodDraftFor(int n) =>
        string.Concat(BuildWork.ReadChunk(Work, n).Lines.Select(l => l.Id + "\tแปลแล้ว\n"));

    [Fact]
    public void Show_prints_the_guide_then_id_tab_english_lines()
    {
        var s = BuildWork.Show(Work, Lib, 1);
        Assert.Contains("STYLE-RULES", s);
        var tail = s[(s.IndexOf("\n---\n", StringComparison.Ordinal) + 5)..];
        Assert.Equal("s000001\tHey, you.\ns000002\tMove it\n", tail);
    }

    [Fact]
    public void Submit_writes_out_only_when_ok_and_check_ignores_tmp_files()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Draft(1))!);
        File.WriteAllText(Draft(1), "bad");
        Assert.False(BuildWork.Submit(Work, 1).Ok);
        Assert.False(File.Exists(Out(1)));

        File.WriteAllText(Draft(1), GoodDraftFor(1));
        Assert.True(BuildWork.Submit(Work, 1).Ok);
        Assert.Contains("\"th\":\"แปลแล้ว\"", File.ReadAllText(Out(1)));

        File.WriteAllText(Out(2) + ".tmp", "half");
        var s = BuildWork.CheckAll(Work);
        Assert.Equal(2, s.Total);
        Assert.Equal(new[] { 1 }, s.Passed);
        Assert.Equal(new[] { 2 }, s.Missing);
        Assert.Empty(s.Failed);
        Assert.True(File.Exists(Path.Combine(Work, "status.json")));
    }

    [Fact]
    public void Submit_without_a_draft_fails_with_a_reason()
    {
        var r = BuildWork.Submit(Work, 2);
        Assert.False(r.Ok);
        Assert.Contains(r.Problems, p => p.Contains("draft"));
    }

    [Fact]
    public void CheckAll_reports_an_out_file_that_no_longer_passes()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Out(1))!);
        File.WriteAllText(Out(1), "{\"id\":\"s000001\",\"th\":\"แปล\"}\n");
        var s = BuildWork.CheckAll(Work);
        Assert.Contains(s.Failed[1], p => p.Contains("s000002") && p.Contains("missing"));
    }
}
