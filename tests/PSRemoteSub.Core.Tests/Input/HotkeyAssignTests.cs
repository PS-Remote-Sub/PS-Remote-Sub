using PSRemoteSub.Core.Input;

namespace PSRemoteSub.Core.Tests.Input;

public sealed class HotkeyAssignTests
{
    private static Dictionary<HotkeyAction, string> Keys() => new()
    {
        [HotkeyAction.SelectRegion] = "Ctrl+Alt+Shift+R",
        [HotkeyAction.ToggleOverlay] = "Ctrl+Alt+Shift+O",
        [HotkeyAction.TogglePause] = "Ctrl+Alt+Shift+P",
    };

    [Fact]
    public void Conflict_FindsOther_CaseInsensitive()
    {
        Assert.Equal(HotkeyAction.TogglePause,
            HotkeyAssign.ConflictWith(Keys(), HotkeyAction.SelectRegion, "ctrl+alt+shift+p"));
    }

    [Fact]
    public void Conflict_IgnoresSelf() =>
        Assert.Null(HotkeyAssign.ConflictWith(Keys(), HotkeyAction.TogglePause, "Ctrl+Alt+Shift+P"));

    [Fact]
    public void Conflict_FreeKey_Null() =>
        Assert.Null(HotkeyAssign.ConflictWith(Keys(), HotkeyAction.SelectRegion, "Ctrl+Alt+Shift+Z"));

    [Fact]
    public void Swap_ExchangesValues()
    {
        var k = Keys();
        HotkeyAssign.Swap(k, HotkeyAction.SelectRegion, HotkeyAction.TogglePause);
        Assert.Equal("Ctrl+Alt+Shift+P", k[HotkeyAction.SelectRegion]);
        Assert.Equal("Ctrl+Alt+Shift+R", k[HotkeyAction.TogglePause]);
    }
}
