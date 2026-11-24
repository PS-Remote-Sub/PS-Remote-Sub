using PSRemoteSub.Core.Platform;
using PSRemoteSub.Windows.Shell.Interop;

namespace PSRemoteSub.Windows.Shell;

/// <summary>
/// Prevents sleep and display-off via SetThreadExecutionState. The state is per thread, so
/// <see cref="SetActive"/> must always be called from the same thread (the UI thread).
/// </summary>
public sealed class Win32KeepAwake : IKeepAwake
{
    public void SetActive(bool active) =>
        ShellNative.SetThreadExecutionState(active
            ? ShellNative.ES_CONTINUOUS | ShellNative.ES_SYSTEM_REQUIRED | ShellNative.ES_DISPLAY_REQUIRED
            : ShellNative.ES_CONTINUOUS);
}
