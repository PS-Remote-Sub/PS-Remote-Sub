using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Text;

namespace PSRemoteSub.Core.Tests.Text;

public class LineJoinerTests
{
    private static OcrLine L(string t, int x, int y) => new(t, new PixelRect(x, y, 100, 20));

    [Fact] public void Join_En_SortsByYAndJoinsWithSpace() =>
        Assert.Equal("We have to leave before sunset.", LineJoiner.Join(
            [L("before sunset.", 0, 50), L("We have to leave", 0, 10)], SourceLanguages.Get("en")));

    [Fact] public void Join_Ja_RemovesCjkSpaces() =>
        Assert.Equal("今日はいい天気", LineJoiner.Join(
            [L("今日 は", 0, 10), L("いい 天気", 0, 40)], SourceLanguages.Get("ja")));

    [Fact] public void Join_Zh_KeepsSpaceNextToLatin() =>
        Assert.Equal("你好 PS5 世界", LineJoiner.Join(
            [L("你好 PS5 世 界", 0, 10)], SourceLanguages.Get("zh")));

    [Fact] public void Join_Ko_KeepsSpacesAndJoinsWithSpace() =>
        Assert.Equal("안녕 하세요 친구", LineJoiner.Join(
            [L("안녕 하세요", 0, 10), L("친구", 0, 40)], SourceLanguages.Get("ko")));

    [Fact] public void Join_SameY_SortsByX() =>
        Assert.Equal("a b", LineJoiner.Join([L("b", 50, 10), L("a", 0, 10)], SourceLanguages.Get("en")));
}
