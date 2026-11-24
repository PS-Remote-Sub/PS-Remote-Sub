using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Text;

namespace PSRemoteSub.Core.Tests.Text;

public class BubbleSplitterTests
{
    private static readonly SourceLanguage En = SourceLanguages.Get("en");
    private static OcrLine L(string t, int y, int w = 260, int h = 20) => new(t, new PixelRect(0, y, w, h));

    [Fact]
    public void PhoneChat_ThreeBubbles()
    {
        var lines = new List<OcrLine> { L("Cal", 0, 40) };
        int y = 32;
        foreach (var t in new[] { "Cops are all over", "the highway again,", "so take the back", "road to the dock." })
        {
            lines.Add(L(t, y));
            y += 30; // height 20 + line gap 10
        }
        y += 35; // the last line's bottom is at y - 10, so the next bubble starts 45 px lower
        foreach (var t in new[] { "ok", "on my way", "be there soon", "keep the engine on", "see you" })
        {
            lines.Add(L(t, y));
            y += 30;
        }
        y += 35;
        lines.Add(L("thanks", y));
        lines.Add(L("see you", y + 30));

        var reads = BubbleSplitter.Split(lines, En);

        Assert.Equal(3, reads.Count);
        Assert.Equal("Cal", reads[0].Name);
        Assert.StartsWith("Cops", reads[0].Text);
        Assert.DoesNotContain("Cal", reads[0].Text);
        Assert.Null(reads[0].LabelBox);
        Assert.Equal(0, reads[0].Box!.Value.Y);
        Assert.Null(reads[1].Name);
        Assert.Null(reads[2].Name);
    }

    [Fact]
    public void WrappedTwoLineSubtitle_StaysOne()
    {
        var reads = BubbleSplitter.Split([L("We have to leave before", 10, 500, 34), L("sunset tonight.", 80, 300, 34)], En);

        var read = Assert.Single(reads);
        Assert.Null(read.Name);
    }

    [Fact]
    public void ShortBubbleAlone_IsNotAName()
    {
        var reads = BubbleSplitter.Split([L("Ok", 0, 40), L("Meet me at the docks tonight", 65)], En);

        Assert.Equal(2, reads.Count);
        Assert.All(reads, r => Assert.Null(r.Name));
    }

    [Fact]
    public void FirstLineWithPunctuation_IsNotAName()
    {
        var reads = BubbleSplitter.Split([L("Bad news.", 0, 60), L("They found the car outside", 30), L("the old warehouse.", 60)], En);

        var read = Assert.Single(reads);
        Assert.Null(read.Name);
    }

    [Fact]
    public void NameLabelInsideBubble_StillSplitsBySpeaker()
    {
        var reads = BubbleSplitter.Split([L("Lucia: hi", 0), L("Jason: yo", 30)], En);

        Assert.Equal(["Lucia", "Jason"], reads.Select(r => r.Name));
    }
}
