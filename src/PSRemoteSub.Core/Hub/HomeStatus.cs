namespace PSRemoteSub.Core.Hub;

/// <summary>What the Home page leads with.</summary>
public enum HomeState
{
    Translating,
    Paused,
    NeedsFix,
}

/// <summary>A problem the Home page lists, in the order <see cref="HomeStatus.Summarize"/> returns them (fix order).</summary>
public enum HomeIssue
{
    NoSource,
    Minimized,
    FramesStalled,
    OcrUnavailable,
    NoRegion,
    AiError,
}

public sealed record HomeSummary(HomeState State, IReadOnlyList<HomeIssue> Issues);

/// <summary>
/// Turns <see cref="PipelineStatus"/> into the Home page's state and ordered fix list. Mirrors the tray's status line
/// (paused wins; the AI error counts only while it is still news) so the two never disagree.
/// </summary>
public static class HomeStatus
{
    public static HomeSummary Summarize(PipelineStatus s, bool paused, DateTimeOffset now)
    {
        if (paused || s.Paused)
            return new HomeSummary(HomeState.Paused, []);

        var issues = new List<HomeIssue>();
        if (!s.RemotePlayFound) issues.Add(HomeIssue.NoSource);
        else if (s.Minimized) issues.Add(HomeIssue.Minimized);
        else if (s.FramesStalled) issues.Add(HomeIssue.FramesStalled);
        if (s.OcrUnavailable) issues.Add(HomeIssue.OcrUnavailable);
        if (s.NoRegion) issues.Add(HomeIssue.NoRegion);
        if (s.AiError is not null && (s.AiRetryAt is not { } retry || retry > now)) issues.Add(HomeIssue.AiError);

        return new HomeSummary(issues.Count == 0 ? HomeState.Translating : HomeState.NeedsFix, issues);
    }

    /// <summary>
    /// Whether launch should open the Home page on its own: only for problems fixed in the app. No picture source yet
    /// (Remote Play not open, frames not flowing) means the game is not running, not that the app needs attention.
    /// </summary>
    public static bool ShouldAutoOpen(HomeSummary summary) =>
        summary.State == HomeState.NeedsFix
        && summary.Issues.Any(i => i is not (HomeIssue.NoSource or HomeIssue.FramesStalled));
}
