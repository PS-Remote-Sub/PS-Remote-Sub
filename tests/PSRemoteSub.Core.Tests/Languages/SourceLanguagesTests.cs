using PSRemoteSub.Core.Languages;

namespace PSRemoteSub.Core.Tests.Languages;

public class SourceLanguagesTests
{
    [Fact] public void SourceLanguages_Get_zh_HasOcrTag_zhHansCN() =>
        Assert.Equal("zh-Hans-CN", SourceLanguages.Get("zh").OcrTag);

    [Fact] public void SourceLanguages_Get_Unknown_Throws() =>
        Assert.Throws<ArgumentException>(() => SourceLanguages.Get("xx"));

    [Theory]
    [InlineData("en", "en")] [InlineData("ja", "ja")] [InlineData("ko", "ko")] [InlineData("zh", "zh-CN")] [InlineData("th", "th")]
    public void GoogleCode_Maps(string code, string google) => Assert.Equal(google, SourceLanguages.GoogleCode(code));
}
