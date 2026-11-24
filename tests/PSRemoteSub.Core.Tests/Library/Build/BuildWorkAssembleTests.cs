using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Library.Build;

namespace PSRemoteSub.Core.Tests.Library.Build;

public sealed class BuildWorkAssembleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lassemble-" + Guid.NewGuid().ToString("N"));
    private string Work => Path.Combine(_root, "work");
    private string Lib => Path.Combine(_root, "gta6");

    public BuildWorkAssembleTests()
    {
        Directory.CreateDirectory(Lib);
        File.WriteAllText(Path.Combine(Lib, "translation-guide.md"), "S");
        var csv = Path.Combine(_root, "d.csv");
        File.WriteAllText(csv, "part,scene,scene_id,line,speaker,text,raw_text,hash\n" +
            "Main story (story000),1,m@1,1,Jason,\"~sl:0~Hey, you.~sl:1~Move it\",x,AA\n" +
            "Open world / side content (data.rpf),2,w@1,1,Unknown,In Spanish: Hola amigo.,x,CC\n");
        BuildWork.Import(csv, Work);
        Directory.CreateDirectory(Path.GetDirectoryName(BuildWork.DraftPath(Work, 1))!);
        File.WriteAllText(BuildWork.DraftPath(Work, 1), "s000001\tเฮ้ย นาย\ns000002\tไปเร็ว\n");
        Assert.True(BuildWork.Submit(Work, 1).Ok);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Assemble_writes_only_passed_chunks_and_loads_back()
    {
        var n = BuildWork.Assemble(Work, Lib, new LibraryVersion(20261122, 1));
        var data = LibraryLoader.Load(Lib, "gta6");
        Assert.NotNull(data);
        Assert.Equal(2, n);
        Assert.Equal(n, data!.Lines.Count);
        Assert.All(data.Lines, l => Assert.Equal("haiku", l.Source));
        Assert.Equal(new[] { "Hey, you.", "Move it" }, data.Lines.Select(l => l.En));
        Assert.Equal("เฮ้ย นาย", data.Lines[0].Th);
        Assert.Equal("m@1", data.Lines[0].Scene);
        Assert.Equal("Jason", data.Lines[0].Speaker);
        Assert.Equal("20261122.1", data.Version.ToString());
        Assert.Equal(data.LinesSha256, data.Sha256);
    }
}
