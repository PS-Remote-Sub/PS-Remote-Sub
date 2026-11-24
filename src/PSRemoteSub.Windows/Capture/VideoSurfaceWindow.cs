using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Windows.Capture.Interop;

namespace PSRemoteSub.Windows.Capture;

/// <summary>
/// A plain native child window (class <c>"PSRemoteSubVideo"</c>, black background) that the capture window hosts and
/// <see cref="VideoPresenter"/> draws into.
/// </summary>
/// <remarks>
/// The window belongs to the thread that calls <see cref="Create"/>, which must run a message loop (the UI thread) and
/// must also call <see cref="Dispose"/>. Dispose any <see cref="VideoPresenter"/> on it first. Events are raised on
/// that thread.
/// </remarks>
public sealed class VideoSurfaceWindow : IDisposable
{
    private const string ClassName = "PSRemoteSubVideo";

    public const int VK_ESCAPE = 0x1B;
    public const int VK_F11 = 0x7A;

    // The class is registered once per process; its WndProc delegate stays rooted here for the process lifetime.
    private static readonly User32.WndProc ClassWndProc = WndProc;
    private static readonly ConcurrentDictionary<nint, VideoSurfaceWindow> Windows = new();
    private static readonly Lock RegisterGate = new();
    private static bool _registered;  // under RegisterGate

    private nint _handle;

    private VideoSurfaceWindow(nint handle) => _handle = handle;

    /// <summary>The child window; 0 once disposed.</summary>
    public nint Handle => _handle;

    /// <summary>A left-button double-click on the picture.</summary>
    public event EventHandler? DoubleClicked;

    /// <summary>A key pressed while the surface has focus, as its virtual-key code (e.g. <see cref="VK_F11"/>, <see cref="VK_ESCAPE"/>).</summary>
    public event EventHandler<int>? KeyPressed;

    /// <summary>Creates the surface as a visible child of <paramref name="parentHwnd"/>, filling its client area.</summary>
    public static VideoSurfaceWindow Create(nint parentHwnd)
    {
        if (parentHwnd == 0 || !User32.IsWindow(parentHwnd))
            throw new ArgumentException("The parent window does not exist.", nameof(parentHwnd));
        nint instance = Kernel32.GetModuleHandleW(null);
        EnsureClassRegistered(instance);

        User32.GetClientRect(parentHwnd, out RECT client);
        nint handle = User32.CreateWindowExW(
            0, ClassName, null, User32.WS_CHILD | User32.WS_VISIBLE | User32.WS_CLIPSIBLINGS,
            0, 0, client.Right - client.Left, client.Bottom - client.Top, parentHwnd, 0, instance, 0);
        if (handle == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the video surface window.");

        var window = new VideoSurfaceWindow(handle);
        Windows[handle] = window;
        return window;
    }

    /// <summary>Destroys the window (on the thread that created it). Does nothing if the parent already destroyed it.</summary>
    public void Dispose()
    {
        nint handle = _handle;
        if (handle == 0)
            return;
        _handle = 0;
        Windows.TryRemove(handle, out _);
        if (User32.IsWindow(handle) && !User32.DestroyWindow(handle))
            Log.Warn($"Could not destroy the video surface window (error {Marshal.GetLastPInvokeError()}); dispose it on the UI thread.");
    }

    private static void EnsureClassRegistered(nint instance)
    {
        lock (RegisterGate)
        {
            if (_registered)
                return;
            var windowClass = new WNDCLASSEXW
            {
                Size = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                Style = User32.CS_DBLCLKS,
                WndProc = Marshal.GetFunctionPointerForDelegate(ClassWndProc),
                Instance = instance,
                Cursor = User32.LoadCursorW(0, User32.IDC_ARROW),
                Background = Gdi32.GetStockObject(Gdi32.BLACK_BRUSH),
                // Never freed: the class stays registered for the life of the process.
                ClassName = Marshal.StringToHGlobalUni(ClassName),
            };
            if (User32.RegisterClassExW(windowClass) == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != User32.ERROR_CLASS_ALREADY_EXISTS)
                {
                    Marshal.FreeHGlobal(windowClass.ClassName);
                    throw new Win32Exception(error, "Could not register the video surface window class.");
                }
            }
            _registered = true;
        }
    }

    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case User32.WM_LBUTTONDOWN:
                    User32.SetFocus(hwnd);
                    return 0;
                case User32.WM_LBUTTONDBLCLK:
                    if (Windows.TryGetValue(hwnd, out var clicked))
                        clicked.DoubleClicked?.Invoke(clicked, EventArgs.Empty);
                    return 0;
                case User32.WM_KEYDOWN:
                    if (Windows.TryGetValue(hwnd, out var focused))
                        focused.KeyPressed?.Invoke(focused, (int)wParam);
                    return 0;
                case User32.WM_NCDESTROY:
                    // Destroyed with its parent: forget it so the handle cannot be confused with a later window.
                    if (Windows.TryRemove(hwnd, out var destroyed))
                        destroyed._handle = 0;
                    break;
            }
        }
        catch (Exception ex)
        {
            // Never let an exception unwind into user32.
            Log.Warn($"Video surface window message 0x{msg:X4} handler failed", ex);
        }
        return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
