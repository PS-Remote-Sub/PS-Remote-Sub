using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PSRemoteSub.Core.Input;
using PSRemoteSub.Windows.Capture.Interop;
using PSRemoteSub.Windows.Shell.Interop;

namespace PSRemoteSub.Windows.Shell;

/// <summary>
/// System-wide hotkeys via RegisterHotKey on a message-only window that lives on its own thread.
/// <see cref="Pressed"/> is raised on that thread; the App must marshal to the UI thread.
/// Register/UnregisterAll are marshalled onto the window thread (hotkeys are bound to the registering thread).
/// </summary>
public sealed class Win32GlobalHotkeys : IGlobalHotkeys
{
    private const uint WM_RUN = ShellNative.WM_APP + 1;

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Dictionary<int, HotkeyAction> _idToAction = new();
    private readonly Dictionary<HotkeyAction, int> _actionToId = new();
    private readonly ShellNative.WndProc _wndProc;
    private readonly string _className = "PSRemoteSubHotkeys_" + Guid.NewGuid().ToString("N");
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private nint _hwnd;
    private uint _threadId;
    private int _nextId = 1;
    private volatile bool _disposed;

    public event EventHandler<HotkeyAction>? Pressed;

    public Win32GlobalHotkeys()
    {
        _wndProc = WindowProc;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "Win32GlobalHotkeys" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_hwnd == 0) throw new InvalidOperationException("Could not create the hotkey message window.");
    }

    public bool Register(HotkeyAction action, HotkeySpec spec)
    {
        if (_disposed) return false;
        uint? vk = ToVirtualKey(spec.Key);
        if (vk is null) return false;
        uint mods = ShellNative.MOD_NOREPEAT;
        if (spec.Ctrl) mods |= ShellNative.MOD_CONTROL;
        if (spec.Alt) mods |= ShellNative.MOD_ALT;
        if (spec.Shift) mods |= ShellNative.MOD_SHIFT;

        return RunOnThread(() =>
        {
            if (_actionToId.Remove(action, out var oldId))
            {
                ShellNative.UnregisterHotKey(_hwnd, oldId);
                _idToAction.Remove(oldId);
            }
            int id = _nextId++;
            if (!ShellNative.RegisterHotKey(_hwnd, id, mods, vk.Value)) return false;
            _idToAction[id] = action;
            _actionToId[action] = id;
            return true;
        }, false);
    }

    public void UnregisterAll()
    {
        if (_disposed) return;
        RunOnThread(() => { UnregisterAllCore(); return true; }, false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) User32.PostThreadMessage(_threadId, User32.WM_QUIT, 0, 0);
        if (Thread.CurrentThread != _thread) _thread.Join(2000);
    }

    private static uint? ToVirtualKey(char key) => key switch
    {
        >= 'A' and <= 'Z' => key,
        >= '0' and <= '9' => key,
        _ => null,
    };

    private T RunOnThread<T>(Func<T> func, T fallback)
    {
        if (Thread.CurrentThread == _thread) return func();
        T result = fallback;
        using var done = new ManualResetEventSlim();
        _work.Enqueue(() =>
        {
            try { result = func(); }
            finally { done.Set(); }
        });
        if (!ShellNative.PostMessage(_hwnd, WM_RUN, 0, 0)) return fallback;
        // The window thread may exit (dispose) before running the item; poll so we never hang.
        while (!done.Wait(100))
            if (!_thread.IsAlive) return fallback;
        return result;
    }

    private void UnregisterAllCore()
    {
        foreach (var id in _idToAction.Keys) ShellNative.UnregisterHotKey(_hwnd, id);
        _idToAction.Clear();
        _actionToId.Clear();
    }

    private void ThreadMain()
    {
        nint hInstance = ShellNative.GetModuleHandle(null);
        nint classNamePtr = Marshal.StringToHGlobalUni(_className);
        bool classRegistered = false;
        try
        {
            var wc = new ShellNative.WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = classNamePtr,
            };
            classRegistered = ShellNative.RegisterClass(ref wc) != 0;
            if (classRegistered)
                _hwnd = ShellNative.CreateWindowEx(0, _className, null, 0, 0, 0, 0, 0,
                    ShellNative.HWND_MESSAGE, 0, hInstance, 0);
            _threadId = Kernel32.GetCurrentThreadId();
        }
        finally { _ready.Set(); }

        if (_hwnd == 0)
        {
            if (classRegistered) ShellNative.UnregisterClass(_className, hInstance);
            Marshal.FreeHGlobal(classNamePtr);
            return;
        }

        while (User32.GetMessage(out var msg, 0, 0, 0) > 0)
            User32.DispatchMessage(msg);

        UnregisterAllCore();
        ShellNative.DestroyWindow(_hwnd);
        ShellNative.UnregisterClass(_className, hInstance);
        Marshal.FreeHGlobal(classNamePtr);
    }

    private nint WindowProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == WM_RUN)
        {
            while (_work.TryDequeue(out var item))
            {
                try { item(); } catch { /* item already signals completion in finally */ }
            }
            return 0;
        }
        if (msg == ShellNative.WM_HOTKEY)
        {
            if (_idToAction.TryGetValue((int)wParam, out var action))
            {
                try { Pressed?.Invoke(this, action); }
                catch { /* a throwing subscriber must not kill the hotkey thread */ }
            }
            return 0;
        }
        return ShellNative.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
