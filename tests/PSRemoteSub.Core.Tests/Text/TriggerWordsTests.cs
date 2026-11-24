using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Text;

namespace PSRemoteSub.Core.Tests.Text;

public class TriggerWordsTests
{
    private static List<OcrLine> Lines(params string[] texts) =>
        texts.Select((t, i) => new OcrLine(t, new PixelRect(0, i * 20, 100, 20))).ToList();

    [Fact] public void Parse_SplitsTrimsAndDropsEmpty() =>
        Assert.Equal(["SEND", "back"], TriggerWords.Parse("SEND, back ,,"));

    [Fact] public void Parse_Null_IsEmpty() => Assert.Empty(TriggerWords.Parse(null));

    [Fact] public void Matches_WordInOneOfSeveralLines() =>
        Assert.True(TriggerWords.Matches(Lines("SCROLL", "SEND", "BACK"), ["SEND"]));

    [Fact] public void Matches_IgnoresCase() =>
        Assert.True(TriggerWords.Matches(Lines("SEND"), ["send"]));

    [Fact] public void Matches_FuzzyForFourLetterWords() =>
        Assert.True(TriggerWords.Matches(Lines("SENO"), ["SEND"]));

    [Fact] public void Matches_NoFuzzForThreeLetterWords() =>
        Assert.False(TriggerWords.Matches(Lines("SET"), ["SEN"]));

    [Fact] public void Matches_FarTokenDoesNotMatch() =>
        Assert.False(TriggerWords.Matches(Lines("SENDING"), ["SEND"]));

    [Fact] public void Matches_EmptyWordList_IsFalse() =>
        Assert.False(TriggerWords.Matches(Lines("SEND"), []));

    [Fact] public void Matches_ThaiWordWithToneMark() =>
        Assert.True(TriggerWords.Matches(Lines("กด ส่ง"), ["ส่ง"]));

    [Fact] public void Matches_AccentedWord() =>
        Assert.True(TriggerWords.Matches(Lines("CAFÉ"), ["café"]));

    [Fact] public void Matches_WordWithPunctuation() =>
        Assert.True(TriggerWords.Matches(Lines("Press Auto-skip"), ["Auto-skip"]));

    [Fact] public void Matches_PhraseAcrossSpacing() =>
        Assert.True(TriggerWords.Matches(Lines("Press  send"), ["PRESS SEND"]));
}
