using System.Diagnostics;
using System.Runtime.InteropServices;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Windows.Capture.Interop;

namespace PSRemoteSub.Windows.Capture;

/// <summary>
/// Tracks the main window of the <c>RemotePlay</c> process. Polls every <c>pollInterval</c> to find the window
/// (and notice when it goes away); while found, WinEvent hooks report moves, resizes and minimize/restore.
/// All work runs on one dedicated background thread with a Win32 message loop, so <see cref="Changed"/> is
/// raised on that thread. Rects are physical pixels when the process is PerMonitorV2 DPI aware.
/// </summary>
public sealed class Win32WindowWatcher : IWindowWatcher
{
    private const string ProcessName = "RemotePlay";

    private readonly TimeSpan _pollInterval;
    private readonly User32.WinEventProc _winEventProc;
    private readonly ManualResetEventSlim _loopReady = new();
    private readonly LogThrottle _faults = new();
    private Thread? _thread;
    private uint _threadId;
    private bool _disposed;
    private WindowInfo? _current;

    // Touched only on the watcher thread.
    private nint _hwnd;
    private int _processId;
    private nint _locationHook;
    private nint _minimizeHook;

    public Win32WindowWatcher(TimeSpan pollInterval)
    {
        if (pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "Poll interval must be positive.");
        _pollInterval = pollInterval;
        _winEventProc = OnWinEvent;
    }

    public WindowInfo? Current => Volatile.Read(ref _current);

    /// <summary>Raised on the watcher thread whenever <see cref="Current"/> changes. Handlers must not throw.</summary>
    public event EventHandler<WindowInfo?>? Changed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null)
            throw new InvalidOperationException("Watcher already started.");

        _thread = new Thread(RunLoop) { IsBackground = true, Name = "RemotePlay window watcher" };
        _thread.Start();
        _loopReady.Wait();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_thread is not null)
        {
            User32.PostThreadMessage(_threadId, User32.WM_QUIT, 0, 0);
            if (Thread.CurrentThread != _thread)
                _thread.Join();
        }
        _loopReady.Dispose();
    }

    private void RunLoop()
    {
        _threadId = Kernel32.GetCurrentThreadId();
        User32.PeekMessage(out _, 0, 0, 0, User32.PM_NOREMOVE); // forces creation of this thread's message queue
        nuint timer = User32.SetTimer(0, 0, (uint)Math.Max(1, _pollInterval.TotalMilliseconds), 0);
        _loopReady.Set();

        try
        {
            SafePoll();
            while (User32.GetMessage(out MSG msg, 0, 0, 0) > 0)
            {
                if (msg.Message == User32.WM_TIMER && msg.Hwnd == 0)
                    SafePoll();
                else
                    User32.DispatchMessage(in msg);
            }
        }
        finally
        {
            User32.KillTimer(0, timer);
            Unhook();
        }
    }

    /// <summary>
    /// An exception on this background thread (or inside the WinEvent callback, which is a native callback) would
    /// end the process. Log it and try again on the next poll.
    /// </summary>
    private void SafePoll() => Guard("poll", Poll);

    private void Guard(string what, Action action)
    {
        try
        {
            action();
            _faults.Reset();
        }
        catch (Exception ex)
        {
            if (_faults.ShouldLog(out int count))
                Log.Warn($"Window watcher {what} failed (#{count})", ex);
        }
    }

    /// <summary>
    /// Re-chooses the window on every poll: Remote Play hides its console-select window and streams in another
    /// top-level window, so the window to follow can change while the old one still exists.
    /// </summary>
    private void Poll()
    {
        var (processId, hwnd) = FindStreamWindow(_processId, _hwnd);
        if (hwnd == 0)
        {
            if (_hwnd != 0)
            {
                Unhook();
                _hwnd = 0;
                _processId = 0;
                Publish(null);
            }
            return;
        }

        if (processId != _processId)
        {
            Unhook();
            Hook(processId); // hooks are per process, so they follow every window of it
        }
        if (_hwnd != 0 && hwnd != _hwnd)
            Log.Info("Remote Play window switched");
        _hwnd = hwnd;
        _processId = processId;
        Refresh(); // also a cheap safety net in case a hook event was missed
    }

    private static (int ProcessId, nint Hwnd) FindStreamWindow(int currentProcessId, nint currentHwnd)
    {
        var processes = Process.GetProcessesByName(ProcessName);
        try
        {
            foreach (var process in processes)
            {
                int id;
                try
                {
                    id = process.Id;
                }
                catch (InvalidOperationException)
                {
                    continue; // exited between enumeration and inspection
                }
                nint chosen = RemotePlayWindowChooser.Choose(Candidates((uint)id), id == currentProcessId ? currentHwnd : 0);
                if (chosen != 0)
                    return (id, chosen);
            }
            return (0, 0);
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static List<WindowCandidate> Candidates(uint processId)
    {
        var list = new List<WindowCandidate>();
        User32.EnumWindowsProc callback = (hwnd, _) =>
        {
            User32.GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == processId)
            {
                User32.GetClientRect(hwnd, out RECT client);
                list.Add(new WindowCandidate(
                    hwnd,
                    User32.IsWindowVisible(hwnd),
                    User32.GetWindow(hwnd, User32.GW_OWNER) != 0,
                    User32.IsIconic(hwnd),
                    client.Right - client.Left,
                    client.Bottom - client.Top));
            }
            return 1;
        };
        User32.EnumWindows(Marshal.GetFunctionPointerForDelegate(callback), 0);
        GC.KeepAlive(callback);
        return list;
    }

    private void Hook(int processId)
    {
        nint proc = Marshal.GetFunctionPointerForDelegate(_winEventProc);
        _locationHook = User32.SetWinEventHook(
            User32.EVENT_OBJECT_LOCATIONCHANGE, User32.EVENT_OBJECT_LOCATIONCHANGE,
            0, proc, (uint)processId, 0, User32.WINEVENT_OUTOFCONTEXT);
        _minimizeHook = User32.SetWinEventHook(
            User32.EVENT_SYSTEM_MINIMIZESTART, User32.EVENT_SYSTEM_MINIMIZEEND,
            0, proc, (uint)processId, 0, User32.WINEVENT_OUTOFCONTEXT);
    }

    private void Unhook()
    {
        if (_locationHook != 0)
        {
            User32.UnhookWinEvent(_locationHook);
            _locationHook = 0;
        }
        if (_minimizeHook != 0)
        {
            User32.UnhookWinEvent(_minimizeHook);
            _minimizeHook = 0;
        }
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (hwnd != _hwnd || _hwnd == 0)
            return;
        if (eventType == User32.EVENT_OBJECT_LOCATIONCHANGE
            && (idObject != User32.OBJID_WINDOW || idChild != User32.CHILDID_SELF))
            return;
        Guard("event", Refresh);
    }

    private void Refresh()
    {
        if (!User32.GetClientRect(_hwnd, out RECT client))
            return;
        var origin = new POINT();
        if (!User32.ClientToScreen(_hwnd, ref origin))
            return;

        Publish(new WindowInfo(
            _hwnd,
            _processId,
            new PixelRect(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top),
            User32.IsIconic(_hwnd)));
    }

    private void Publish(WindowInfo? info)
    {
        if (Equals(info, Current))
            return;
        Volatile.Write(ref _current, info);
        Changed?.Invoke(this, info);
    }
}
