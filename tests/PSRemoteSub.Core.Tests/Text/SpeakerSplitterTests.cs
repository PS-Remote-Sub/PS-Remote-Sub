using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Text;

namespace PSRemoteSub.Core.Tests.Text;

public class SpeakerSplitterTests
{
    private static readonly SourceLanguage En = SourceLanguages.Get("en");
    private static OcrLine L(string t, int y) => new(t, new PixelRect(0, y, 300, 20));

    private static IReadOnlyList<(string? Name, string Text)> Split(SourceLanguage lang, params OcrLine[] lines) =>
        [.. SpeakerSplitter.Split(lines, lang).Select(r => (r.Name, r.Text))];

    [Fact]
    public void OneLabel() =>
        Assert.Equal([("Mattia", "[laughs] Madonna, what a duo. Wow.")],
            Split(En, L("Mattia: [laughs] Madonna, what a duo. Wow.", 10)));

    [Fact]
    public void TwoLabels_TwoReads() =>
        Assert.Equal([("Lucia", "Where are you going?"), ("Jason", "Just out.")],
            Split(En, L("Lucia: Where are you going?", 10), L("Jason: Just out.", 40)));

    [Fact]
    public void LabelThenWrappedLine_OneRead() =>
        Assert.Equal([("Lucia", "We have to leave before sunset.")],
            Split(En, L("before sunset.", 40), L("Lucia: We have to leave", 10)));

    [Fact]
    public void TwoDashes_TwoReads_NoName() =>
        Assert.Equal([(null, "- Where?"), (null, "- Out.")], Split(En, L("- Where?", 10), L("- Out.", 40)));

    [Fact]
    public void NoCues_OneRead_SameAsToday()
    {
        OcrLine[] lines = [L("before sunset.", 40), L("We have to leave", 10)];
        Assert.Equal(TextNormalizer.Normalize(LineJoiner.Join(lines, En)),
            SpeakerSplitter.Compose(SpeakerSplitter.Split(lines, En)));
    }

    [Fact]
    public void Time_NotALabel() => Assert.Equal([(null, "10:30 is late")], Split(En, L("10:30 is late", 10)));

    [Fact]
    public void LowerCaseWord_NotALabel() => Assert.Equal([(null, "note: this")], Split(En, L("note: this", 10)));

    [Theory]
    [InlineData("Remember this: we leave at dawn.")]
    [InlineData("He said: go now")]
    [InlineData("Here's the thing: no.")]
    [InlineData("Officer Number Two Three: hold it")]
    public void SentenceBeforeColon_NotALabel(string line) => Assert.Equal([(null, line)], Split(En, L(line, 10)));

    [Theory]
    [InlineData("Mr. Smith: Hi.", "Mr. Smith")]
    [InlineData("LUCIA: Hi.", "LUCIA")]
    [InlineData("Officer Number Two: Hold it.", "Officer Number Two")]
    public void CapitalisedName_IsALabel(string line, string name) => Assert.Equal(name, Split(En, L(line, 10))[0].Name);

    [Fact]
    public void Japanese_FullWidthColon() =>
        Assert.Equal([("ルシア", "どこへ行くの")], Split(SourceLanguages.Get("ja"), L("ルシア：どこへ行くの", 10)));

    [Fact]
    public void NoiseOnlyLine_Dropped() =>
        Assert.Equal([("Jason", "Just out.")], Split(En, L("|", 5), L("Jason: Just out.", 40)));

    [Fact]
    public void LabelBox_CoversNameWordsOnly()
    {
        var line = new OcrLine("Mattia: Wow.", new PixelRect(10, 5, 200, 30),
            [new OcrWord("Mattia:", new PixelRect(10, 5, 90, 30)), new OcrWord("Wow.", new PixelRect(110, 5, 60, 30))]);
        Assert.Equal(new PixelRect(10, 5, 90, 30), Assert.Single(SpeakerSplitter.Split([line], En)).LabelBox);
    }

    [Fact]
    public void LabelBox_NullWithoutWords() =>
        Assert.Null(Assert.Single(SpeakerSplitter.Split([L("Mattia: Wow.", 10)], En)).LabelBox);

    [Fact]
    public void Box_IsTheUnionOfEachSpeakersLines()
    {
        var reads = SpeakerSplitter.Split([
            new OcrLine("Cop: Talk to us,", new PixelRect(100, 10, 200, 20)),
            new OcrLine("Sergeant?", new PixelRect(150, 34, 80, 20)),
            new OcrLine("Jason: C'mon.", new PixelRect(120, 70, 150, 20)),
        ], En);
        Assert.Equal([new PixelRect(100, 10, 200, 44), new PixelRect(120, 70, 150, 20)], reads.Select(r => r.Box));
    }

    [Fact]
    public void Box_IgnoresLinesWithoutPosition()
    {
        var read = Assert.Single(SpeakerSplitter.Split([
            new OcrLine("Lucia: Go.", new PixelRect(10, 10, 100, 20)),
            new OcrLine("now", new PixelRect(0, 40, 0, 0)), // below it, but with no size
        ], En));
        Assert.Equal(new PixelRect(10, 10, 100, 20), read.Box);
    }

    [Fact]
    public void Compose_JoinsWithNewline() =>
        Assert.Equal("Lucia: Where?\n- Out.\nfine", SpeakerSplitter.Compose(
            [new SpeakerRead("Lucia", "Where?", null), new SpeakerRead(null, "- Out.", null), new SpeakerRead(null, "fine", null)]));

    [Fact]
    public void SpeakerLinesOnly_Off_KeepsEveryRead()
    {
        var reads = SpeakerSplitter.Split([L("EVERYDAY DREICH", 10), L("Lucia: Hey, I'm being nice.", 40)], En);
        Assert.Same(reads, SpeakerSplitter.SpeakerLinesOnly(reads, enabled: false));
    }

    [Fact]
    public void SpeakerLinesOnly_DropsReadsWithoutAName() =>
        Assert.Equal([("Lucia", "Hey, I'm being nice.")],
            SpeakerSplitter.SpeakerLinesOnly(
                SpeakerSplitter.Split([L("EVERYDAY DREICH", 10), L("Lucia: Hey, I'm being nice.", 40)], En), enabled: true)
                .Select(r => (r.Name, r.Text)));

    [Fact]
    public void SpeakerLinesOnly_NoNameAtAll_Nothing() =>
        Assert.Empty(SpeakerSplitter.SpeakerLinesOnly(SpeakerSplitter.Split([L("CHIPS", 10)], En), enabled: true));
}
