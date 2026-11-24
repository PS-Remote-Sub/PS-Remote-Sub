namespace PSRemoteSub.Windows.Capture;

/// <summary>A top-level window of the Remote Play process, as seen by one poll.</summary>
public readonly record struct WindowCandidate(nint Handle, bool Visible, bool Owned, bool Minimized, int ClientWidth, int ClientHeight)
{
    public long ClientArea => (long)Math.Max(ClientWidth, 0) * Math.Max(ClientHeight, 0);
}

/// <summary>
/// Picks the Remote Play window to capture. Remote Play keeps its console-select window alive but hidden while it
/// streams in a second top-level window, and owns tool windows (toolbar, volume) of its own, so
/// <c>Process.MainWindowHandle</c> and "keep the first window while it exists" both pick the wrong one.
/// </summary>
public static class RemotePlayWindowChooser
{
    /// <summary>
    /// Only visible, unowned windows qualify (a minimized window is still visible). The current window is kept while it
    /// qualifies, so the choice does not flap; otherwise a restored window beats a minimized one, then the larger
    /// client area wins. Returns 0 when nothing qualifies.
    /// </summary>
    public static nint Choose(IReadOnlyList<WindowCandidate> candidates, nint current)
    {
        nint best = 0;
        WindowCandidate bestCandidate = default;
        foreach (var c in candidates)
        {
            if (!c.Visible || c.Owned || c.Handle == 0)
                continue;
            if (c.Handle == current)
                return current;
            if (best == 0 || IsBetter(c, bestCandidate))
            {
                best = c.Handle;
                bestCandidate = c;
            }
        }
        return best;
    }

    private static bool IsBetter(WindowCandidate a, WindowCandidate b)
    {
        if (a.Minimized != b.Minimized)
            return !a.Minimized;
        return a.ClientArea > b.ClientArea;
    }
}
