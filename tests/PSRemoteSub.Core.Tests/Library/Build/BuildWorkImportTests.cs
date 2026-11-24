using PSRemoteSub.Core.Library.Build;

namespace PSRemoteSub.Core.Tests.Library.Build;

public sealed class BuildWorkImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lbuild-" + Guid.NewGuid().ToString("N"));
    private string Csv => Path.Combine(_root, "d.csv");
    private string Work => Path.Combine(_root, "work");

    public BuildWorkImportTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Csv, "﻿part,scene,scene_id,line,speaker,text,raw_text,hash\n" +
            "Main story (story000),1,m@1,1,Jason,\"~sl:0~Hey, you.~sl:1~Move it\",x,AA\n" +
            "Main story (story000),1,m@1,2,Unknown,Hey you!,x,BB\n" +
            "Open world / side content (data.rpf),2,w@1,1,Unknown,In Spanish: Hola amigo.,x,CC\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Import_writes_segments_chunks_and_manifest()
    {
        var r = BuildWork.Import(Csv, Work);
        Assert.Equal(new ImportResult(3, 3, 2), r);
        Assert.Equal(2, BuildWork.ChunkCount(Work));
        var c = BuildWork.ReadChunk(Work, 1);
        Assert.Equal(new[] { "s000001", "s000002" }, c.Lines.Select(l => l.Id));
        Assert.Equal("Hey, you.", c.Lines[0].En);
        Assert.Equal("Jason", c.Lines[0].Speaker);
        Assert.Equal("m@1", c.Lines[0].Scene);
        Assert.True(File.Exists(Path.Combine(Work, "segments.jsonl")));
        Assert.True(File.Exists(Path.Combine(Work, "manifest.json")));
    }

    [Fact]
    public void Import_with_identical_result_leaves_the_chunks_folder_alone()
    {
        BuildWork.Import(Csv, Work);
        var marker = Path.Combine(Work, "chunks", "agent-was-here.txt");
        File.WriteAllText(marker, "x");
        BuildWork.Import(Csv, Work);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void Import_refuses_a_different_chunking_of_the_same_csv_once_out_exists()
    {
        BuildWork.Import(Csv, Work);
        Directory.CreateDirectory(Path.Combine(Work, "out"));
        File.WriteAllText(Path.Combine(Work, "out", "0001.jsonl"), "{}");
        var first = File.ReadAllText(Path.Combine(Work, "chunks", "0001.jsonl"));
        Assert.Throws<InvalidOperationException>(() => BuildWork.Import(Csv, Work, maxChunk: 1));
        Assert.Equal(first, File.ReadAllText(Path.Combine(Work, "chunks", "0001.jsonl")));
    }

    [Fact]
    public void Import_twice_gives_identical_ids_and_refuses_a_changed_csv_once_out_exists()
    {
        BuildWork.Import(Csv, Work);
        var first = File.ReadAllText(Path.Combine(Work, "chunks", "0001.jsonl"));
        BuildWork.Import(Csv, Work);
        Assert.Equal(first, File.ReadAllText(Path.Combine(Work, "chunks", "0001.jsonl")));
        Directory.CreateDirectory(Path.Combine(Work, "out"));
        File.WriteAllText(Path.Combine(Work, "out", "0001.jsonl"), "{}");
        File.AppendAllText(Csv, "Main story (story000),9,z@9,1,Unknown,New line,New line,FF\n");
        Assert.Throws<InvalidOperationException>(() => BuildWork.Import(Csv, Work));
    }
}
