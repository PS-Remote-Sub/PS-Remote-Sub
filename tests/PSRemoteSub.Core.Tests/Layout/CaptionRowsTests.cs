using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Layout;

namespace PSRemoteSub.Core.Tests.Layout;

public sealed class CaptionRowsTests
{
    [Fact]
    public void Split_PlainText_IsOneRowWithoutSpeaker()
    {
        var rows = CaptionRows.Split("สวัสดี", null);
        Assert.Equal([new CaptionRow(null, "สวัสดี")], rows);
    }

    [Fact]
    public void Split_EachTextLineIsARow_WithItsSpeaker()
    {
        var cop = new SpeakerTag("ตำรวจ", "#88CCFF");
        var jason = new SpeakerTag("เจสัน", null);
        var rows = CaptionRows.Split("คุยกับเราสิ จ่า\nเร็วเข้า ต้องถอดกุญแจมือนี่", [cop, jason]);
        Assert.Equal([new CaptionRow(cop, "คุยกับเราสิ จ่า"), new CaptionRow(jason, "เร็วเข้า ต้องถอดกุญแจมือนี่")], rows);
    }

    [Fact]
    public void Split_LinesWithoutSpeakers_AreStillSeparateRows()
    {
        var rows = CaptionRows.Split("บรรทัดหนึ่ง\nบรรทัดสอง", null);
        Assert.Equal([new CaptionRow(null, "บรรทัดหนึ่ง"), new CaptionRow(null, "บรรทัดสอง")], rows);
    }

    [Fact]
    public void Split_FewerSpeakersThanLines_RestHaveNone()
    {
        var a = new SpeakerTag("A", null);
        var rows = CaptionRows.Split("หนึ่ง\nสอง", [a]);
        Assert.Equal([new CaptionRow(a, "หนึ่ง"), new CaptionRow(null, "สอง")], rows);
    }

    [Fact]
    public void Split_BlankLines_AreDropped_SpeakersStayAligned()
    {
        var a = new SpeakerTag("A", null);
        var b = new SpeakerTag("B", null);
        var c = new SpeakerTag("C", null);
        var rows = CaptionRows.Split("หนึ่ง\n  \nสาม", [a, b, c]);
        Assert.Equal([new CaptionRow(a, "หนึ่ง"), new CaptionRow(c, "สาม")], rows);
    }

    [Fact]
    public void Split_WithRowBounds_EachRowGetsItsEnglishLine()
    {
        var a = new RegionFraction(0.1, 0.8, 0.5, 0.05);
        var b = new RegionFraction(0.1, 0.86, 0.6, 0.05);
        var rows = CaptionRows.Split("หนึ่ง\nสอง", null, [a, b]);
        Assert.Equal([a, b], rows.Select(r => r.Cover));
    }

    [Fact]
    public void Split_RowBoundsNotOnePerLine_NoCovers()
    {
        var a = new RegionFraction(0.1, 0.8, 0.5, 0.05);
        Assert.All(CaptionRows.Split("หนึ่ง\nสอง", null, [a]), r => Assert.Null(r.Cover));
    }

    [Fact]
    public void Split_EmptyText_IsOneEmptyRow()
    {
        Assert.Equal([new CaptionRow(null, "")], CaptionRows.Split("", null));
    }

    [Fact]
    public void Split_CrLf_IsTrimmed()
    {
        Assert.Equal([new CaptionRow(null, "หนึ่ง"), new CaptionRow(null, "สอง")], CaptionRows.Split("หนึ่ง\r\nสอง", null));
    }
}
