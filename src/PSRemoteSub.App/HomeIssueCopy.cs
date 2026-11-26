using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App;

/// <summary>The Home page's title and explanation for each <see cref="HomeIssue"/>.</summary>
internal static class HomeIssueCopy
{
    public static (string Title, string Body) For(HomeIssue issue, AppHost host)
    {
        bool card = host.PictureSource == PictureSource.CaptureCard;
        return issue switch
        {
            HomeIssue.NoSource => card
                ? (Loc.Get("IssueNoCaptureTitle"), Loc.Get("IssueNoCaptureBody"))
                : (Loc.Get("IssueNoSourceTitle"), Loc.Get("IssueNoSourceBody")),
            HomeIssue.Minimized => card
                ? (Loc.Get("IssueMinimizedCaptureTitle"), Loc.Get("IssueMinimizedCaptureBody"))
                : (Loc.Get("IssueMinimizedTitle"), Loc.Get("IssueMinimizedBody")),
            HomeIssue.FramesStalled => (Loc.Get("IssueStalledTitle"), Loc.Get("IssueStalledBody")),
            HomeIssue.OcrUnavailable => OcrCopy(host),
            HomeIssue.NoRegion => (Loc.Get("IssueRegionTitle"), Loc.Get("IssueRegionBody")),
            HomeIssue.AiError => (Loc.Get("IssueAiTitle"), host.Status.AiError is { } key ? Loc.Get(key) : ""),
            _ => ("", ""),
        };
    }

    /// <summary>The game's source language in Thai ("อังกฤษ"), or its code when unknown.</summary>
    public static string LanguageName(string code) =>
        SourceLanguages.All.FirstOrDefault(l => l.Code == code)?.ThaiName ?? code;

    private static (string, string) OcrCopy(AppHost host)
    {
        var name = LanguageName(host.Settings.GetActive().SourceLang);
        var body = Loc.Format("IssueOcrBody", name);
        if (host.Platform.Kind == PlatformKind.Windows)
            body += Loc.Get("IssueOcrWinNote");
        return (Loc.Format("IssueOcrTitle", name), body);
    }
}
