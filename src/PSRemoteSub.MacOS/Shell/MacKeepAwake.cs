using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.MacOS.Shell;

/// <summary>Holds an IOPMAssertion that stops the display sleeping while the app is working.</summary>
public sealed class MacKeepAwake : IKeepAwake
{
    public void SetActive(bool active)
    {
        try
        {
            int code = Native.psrs_keepawake_set(active ? 1 : 0);
            if (code != 0) Log.Warn($"Keep-awake {(active ? "on" : "off")} failed (code {code})");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn("Keep-awake unavailable", ex);
        }
    }
}
