using System.Runtime.InteropServices;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Input;

namespace PSRemoteSub.MacOS.Input;

/// <summary>
/// Global hotkeys through Carbon's <c>RegisterEventHotKey</c> (no accessibility permission needed). Carbon delivers a press
/// to the app's event loop, which on Mac is Avalonia's UI thread: create and register from it. Only one instance is live
/// at a time (the native side has one callback).
/// </summary>
public sealed class MacGlobalHotkeys : IGlobalHotkeys
{
    private const uint ControlKey = 0x1000, OptionKey = 0x0800, ShiftKey = 0x0200;

    private static MacGlobalHotkeys? s_current;

    public event EventHandler<HotkeyAction>? Pressed;

    public unsafe MacGlobalHotkeys()
    {
        Volatile.Write(ref s_current, this);
        Native.psrs_hotkey_set_callback(&OnNativePressed);
    }

    public bool Register(HotkeyAction action, HotkeySpec spec)
    {
        try
        {
            uint modifiers = (spec.Ctrl ? ControlKey : 0) | (spec.Alt ? OptionKey : 0) | (spec.Shift ? ShiftKey : 0);
            return Native.psrs_hotkey_register((int)action, MacKeyCodes.ForKey(spec.Key), modifiers) == 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Hotkey register failed for {action}: {ex.Message}");
            return false;
        }
    }

    public void UnregisterAll()
    {
        try { Native.psrs_hotkey_unregister_all(); }
        catch (Exception ex) { Log.Warn($"Hotkey unregister failed: {ex.Message}"); }
    }

    public unsafe void Dispose()
    {
        UnregisterAll();
        if (ReferenceEquals(Interlocked.CompareExchange(ref s_current, null, this), this))
        {
            try { Native.psrs_hotkey_set_callback(null); }
            catch (Exception ex) { Log.Warn($"Hotkey callback reset failed: {ex.Message}"); }
        }
    }

    // Called by native code on the main thread. Must never throw.
    [UnmanagedCallersOnly]
    private static void OnNativePressed(int id)
    {
        try
        {
            if (!Enum.IsDefined(typeof(HotkeyAction), id)) return;
            var self = Volatile.Read(ref s_current);
            self?.Pressed?.Invoke(self, (HotkeyAction)id);
        }
        catch (Exception ex)
        {
            try { Log.Warn($"Hotkey handler failed: {ex.Message}"); } catch { /* nothing more to do */ }
        }
    }
}
