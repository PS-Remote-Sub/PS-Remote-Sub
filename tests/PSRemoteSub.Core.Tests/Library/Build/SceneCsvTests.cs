using PSRemoteSub.Core.Library.Build;

namespace PSRemoteSub.Core.Tests.Library.Build;

public class SceneCsvTests
{
    [Fact]
    public void Read_handles_quotes_commas_and_newlines_in_fields()
    {
        var csv = "﻿part,scene,scene_id,line,speaker,text,raw_text,hash\n" +
                  "Main story (story000),1,a@0x1,1,Unknown,\"Well, look \"\"here\"\"\nnow\",x,AA\n" +
                  "Main story (story000),1,a@0x1,2,Jason,Hi,Hi,BB\n";
        var rows = SceneCsv.Read(new StringReader(csv)).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Well, look \"here\"\nnow", rows[0].Text);
        Assert.Equal(new SceneRow("Main story (story000)", "a@0x1", "Jason", "Hi"), rows[1]);
    }

    [Fact]
    public void Read_accepts_crlf_and_rejects_a_header_without_text()
    {
        var rows = SceneCsv.Read(new StringReader("part,scene_id,speaker,text\r\nP,s,Lucia,Yo\r\n")).ToList();
        Assert.Equal(new SceneRow("P", "s", "Lucia", "Yo"), Assert.Single(rows));
        Assert.Throws<InvalidDataException>(() => SceneCsv.Read(new StringReader("part,scene_id,speaker\nP,s,X\n")).ToList());
    }
}
