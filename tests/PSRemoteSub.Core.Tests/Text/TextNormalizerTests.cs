using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Text;

namespace PSRemoteSub.Core.Tests.Text;

public class TextNormalizerTests
{
    [Fact] public void Normalize_CollapsesAndDropsNoiseTokens() =>
        Assert.Equal("Hello world", TextNormalizer.Normalize("  Hello |  world _ "));

    [Fact] public void HasSubtitle_En_SingleLetter_False() =>
        Assert.False(TextNormalizer.HasSubtitle("I", SourceLanguages.Get("en")));
    [Fact] public void HasSubtitle_En_Go_True() =>
        Assert.True(TextNormalizer.HasSubtitle("Go!", SourceLanguages.Get("en")));
    [Fact] public void HasSubtitle_En_DigitsOnly_False() =>
        Assert.False(TextNormalizer.HasSubtitle("123 ...", SourceLanguages.Get("en")));
    [Fact] public void HasSubtitle_Ja_Hiragana_True() =>
        Assert.True(TextNormalizer.HasSubtitle("はい", SourceLanguages.Get("ja")));
    [Fact] public void HasSubtitle_Zh_Kana_False() =>
        Assert.False(TextNormalizer.HasSubtitle("はい", SourceLanguages.Get("zh")));
    [Fact] public void HasSubtitle_Ko_Hangul_True() =>
        Assert.True(TextNormalizer.HasSubtitle("안녕", SourceLanguages.Get("ko")));
    [Fact] public void Normalize_KeepsNewlines_DropsEmptyLines() =>
        Assert.Equal("Lucia: Where?\nJason: Out.", TextNormalizer.Normalize("Lucia:  Where? \n | \nJason: Out."));
}
