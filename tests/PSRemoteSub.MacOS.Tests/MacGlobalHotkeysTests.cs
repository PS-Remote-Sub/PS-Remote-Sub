using PSRemoteSub.Core.Input;
using PSRemoteSub.MacOS.Input;

namespace PSRemoteSub.MacOS.Tests;

public class MacGlobalHotkeysTests
{
    // Registering needs no running event loop (only delivering a press does, which the manual check covers).
    [MacFact]
    public void Register_FreeCombination_ReturnsTrue_AndUnregisterAllReleasesIt()
    {
        using var hotkeys = new MacGlobalHotkeys();
        var spec = HotkeySpec.Parse("Ctrl+Alt+Shift+9");
        Assert.True(hotkeys.Register(HotkeyAction.SaveFrame, spec));
        hotkeys.UnregisterAll();
        Assert.True(hotkeys.Register(HotkeyAction.SaveFrame, spec));
    }

    [MacFact]
    public void Register_SameCombinationTwice_SecondReturnsFalse()
    {
        using var hotkeys = new MacGlobalHotkeys();
        var spec = HotkeySpec.Parse("Ctrl+Alt+Shift+8");
        Assert.True(hotkeys.Register(HotkeyAction.SaveFrame, spec));
        Assert.False(hotkeys.Register(HotkeyAction.TogglePause, spec));
    }
}
