using PSRemoteSub.Core.Input;

namespace PSRemoteSub.Core.Tests.Input;

public class HotkeySpecTests
{
    [Theory]
    [InlineData("Ctrl+Alt+S", true, true, false, 'S')]
    [InlineData("ctrl + shift + 7", true, false, true, '7')]
    public void Parse_Valid(string text, bool c, bool a, bool s, char k) =>
        Assert.Equal(new HotkeySpec(c, a, s, k), HotkeySpec.Parse(text));

    [Theory] [InlineData("S")] [InlineData("Ctrl+F13")] [InlineData("")]
    public void Parse_RejectsNoModifierOrBadKey(string text) =>
        Assert.Throws<FormatException>(() => HotkeySpec.Parse(text));

    [Fact] public void ToString_RoundTrips() =>
        Assert.Equal("Ctrl+Alt+S", HotkeySpec.Parse("alt+ctrl+s").ToString());
}
