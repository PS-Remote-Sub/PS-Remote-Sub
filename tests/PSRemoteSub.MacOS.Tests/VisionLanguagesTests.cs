using PSRemoteSub.MacOS.Ocr;

namespace PSRemoteSub.MacOS.Tests;

public class VisionLanguagesTests
{
    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData("ja-JP", "ja-JP")]
    [InlineData("ko-KR", "ko-KR")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    public void ToVision_MapsOcrTag(string tag, string expected) => Assert.Equal(expected, VisionLanguages.ToVision(tag));
}
