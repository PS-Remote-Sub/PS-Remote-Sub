using PSRemoteSub.Core.Pipeline;

namespace PSRemoteSub.Core.Tests.Pipeline;

public class PartMemoryTests
{
    [Fact]
    public void PartMemory_GetsWhatWasPut()
    {
        var memory = new PartMemory();

        memory.Put("Hello there", "สวัสดี");

        Assert.Equal("สวัสดี", memory.Get("Hello there"));
        Assert.Null(memory.Get("See you"));
    }

    [Fact]
    public void PartMemory_EvictsTheLeastRecentPastCapacity()
    {
        var memory = new PartMemory();
        for (int i = 0; i < PartMemory.Capacity; i++)
            memory.Put("p" + i, "t" + i);
        Assert.Equal("t0", memory.Get("p0")); // p0 is now the most recent

        memory.Put("new", "ใหม่");

        Assert.Equal("t0", memory.Get("p0"));
        Assert.Null(memory.Get("p1"));
        Assert.Equal("ใหม่", memory.Get("new"));
    }

    [Fact]
    public void PartMerge_FillsUnknownPartsFromTheAnswerInOrder()
    {
        Assert.Equal("ก\nai:B\nค\nai:D", PartMerge.Merge(["ก", null, "ค", null], "ai:B\nai:D"));
    }

    [Fact]
    public void PartMerge_AnswerBlankLinesDropped()
    {
        Assert.Equal("ก\nข\nค", PartMerge.Merge([null, "ข", null], " ก \r\n\nค\n"));
    }

    [Theory]
    [InlineData("one line")]
    [InlineData("a\nb\nc")]
    [InlineData("")]
    public void PartMerge_LineCountMismatch_Null(string answer)
    {
        Assert.Null(PartMerge.Merge(["ก", null, "ค", null], answer));
    }

    [Fact]
    public void PartMerge_Unknown_JoinsOnlyTheUnknownParts()
    {
        Assert.Equal("B\nD", PartMerge.Unknown(["A", "B", "C", "D"], ["ก", null, "ค", null]));
    }
}
