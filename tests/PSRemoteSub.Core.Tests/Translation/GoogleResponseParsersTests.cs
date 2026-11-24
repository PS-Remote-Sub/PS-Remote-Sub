using PSRemoteSub.Core.Translation;

namespace PSRemoteSub.Core.Tests.Translation;

public class GoogleResponseParsersTests
{
    internal static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "google", name));

    [Fact]
    public void ParseGtx_SingleSegment() =>
        Assert.Equal("สวัสดีชาวโลก", GoogleResponseParsers.ParseGtx(Fixture("gtx-en-th.json")));

    [Fact]
    public void ParseGtx_JoinsAllSegments() =>
        Assert.Equal("ฉันอยู่ที่นี่ คุณอยู่ที่ไหน", GoogleResponseParsers.ParseGtx(Fixture("gtx-multi-segment.json")));

    [Fact]
    public void ParseClients5_BothShapes()
    {
        Assert.Equal("สวัสดี", GoogleResponseParsers.ParseClients5("[\"สวัสดี\"]"));
        Assert.Equal("สวัสดี", GoogleResponseParsers.ParseClients5("[[\"สวัสดี\",\"en\"]]"));
        Assert.Equal("สวัสดีชาวโลก", GoogleResponseParsers.ParseClients5(Fixture("clients5-en-th.json")));
    }

    // Real clients5 response; for this phrase it is a single element holding the full sentence.
    [Fact]
    public void ParseClients5_MultiSegment_JoinsAll() =>
        Assert.Equal("ฉันอยู่ที่นี่ คุณอยู่ที่ไหน", GoogleResponseParsers.ParseClients5(Fixture("clients5-multi-segment.json")));

    [Fact]
    public void ParseClients5_MultiElement_JoinsAll()
    {
        Assert.Equal("AB", GoogleResponseParsers.ParseClients5("[\"A\",\"B\"]"));
        Assert.Equal("AB", GoogleResponseParsers.ParseClients5("[[\"A\",\"en\"],[\"B\",\"en\"]]"));
    }

    [Fact]
    public void ParseGtx_Html_ThrowsFormat() =>
        Assert.Throws<FormatException>(() => GoogleResponseParsers.ParseGtx(Fixture("captcha.html")));

    [Fact]
    public void ParseClients5_Html_ThrowsFormat() =>
        Assert.Throws<FormatException>(() => GoogleResponseParsers.ParseClients5(Fixture("captcha.html")));

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[[]]")]
    [InlineData("[[[1]]]")]
    public void ParseGtx_UnexpectedShape_ThrowsFormat(string json) =>
        Assert.Throws<FormatException>(() => GoogleResponseParsers.ParseGtx(json));
}
