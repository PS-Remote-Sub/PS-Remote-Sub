using PSRemoteSub.MacOS.Input;

namespace PSRemoteSub.MacOS.Tests;

public class MacKeyCodesTests
{
    [Theory]
    [InlineData('A', 0x00)]
    [InlineData('S', 0x01)]
    [InlineData('0', 0x1D)]
    [InlineData('9', 0x19)]
    [InlineData('Z', 0x06)]
    [InlineData('1', 0x12)]
    public void ForKey_IsAnsiVirtualKeyCode(char key, int code) => Assert.Equal(code, MacKeyCodes.ForKey(key));

    [Fact]
    public void ForKey_Unsupported_Throws() => Assert.Throws<ArgumentOutOfRangeException>(() => MacKeyCodes.ForKey('-'));
}
