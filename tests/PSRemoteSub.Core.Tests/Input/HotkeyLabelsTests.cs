using PSRemoteSub.Core.Input;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.Core.Tests.Input;

public class HotkeyLabelsTests
{
    [Fact]
    public void Display_Windows_SameAsToString()
    {
        var spec = HotkeySpec.Parse("Ctrl+Alt+Shift+S");
        Assert.Equal(spec.ToString(), HotkeyLabels.Display(spec, PlatformKind.Windows));
    }

    [Fact]
    public void Display_Mac_CtrlAltShiftS_IsSymbols() =>
        Assert.Equal("⌃⌥⇧S", HotkeyLabels.Display(HotkeySpec.Parse("Ctrl+Alt+Shift+S"), PlatformKind.MacOS));

    [Fact]
    public void Display_Mac_AltOnly() =>
        Assert.Equal("⌥P", HotkeyLabels.Display(HotkeySpec.Parse("Alt+P"), PlatformKind.MacOS));

    [Theory]
    [InlineData(PlatformKind.Windows)]
    [InlineData(PlatformKind.MacOS)]
    public void DisplaySetting_Unparsable_ShowsTheSettingText(PlatformKind kind) =>
        Assert.Equal("Hyper+S", HotkeyLabels.DisplaySetting("Hyper+S", kind));

    [Fact]
    public void DisplaySetting_Parsable_ShowsTheLabel() =>
        Assert.Equal("⌃⌥⇧S", HotkeyLabels.DisplaySetting("Ctrl+Alt+Shift+S", PlatformKind.MacOS));

    [Fact]
    public void DisplaySetting_Empty_Empty() =>
        Assert.Equal("", HotkeyLabels.DisplaySetting("", PlatformKind.Windows));
}
