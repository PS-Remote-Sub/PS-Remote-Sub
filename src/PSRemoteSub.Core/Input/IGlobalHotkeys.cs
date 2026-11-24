namespace PSRemoteSub.Core.Input;

public interface IGlobalHotkeys : IDisposable
{
    bool Register(HotkeyAction action, HotkeySpec spec);
    void UnregisterAll();
    event EventHandler<HotkeyAction>? Pressed;
}
