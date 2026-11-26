using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.App;

/// <summary>
/// The ids of notice buttons, all handled in one place (<see cref="AppHost"/>'s notice action handler). There is no
/// "open the Remote Play window" button: the app cannot bring another app's window back.
/// </summary>
internal static class NoticeActionIds
{
    public const string OpenCapture = "open-capture";
    public const string SelectRegion = "select-region";
    public const string UpdateNow = "update-now";
    public const string UpdateLater = "update-later";
    public const string PagePrefix = "page:";
    public const string PermissionPrefix = "permission:";

    /// <summary>Opens the main window on <paramref name="page"/>.</summary>
    public static string OpenPage(MainPage page) => PagePrefix + page;

    /// <summary>Opens the system settings for <paramref name="permission"/>.</summary>
    public static string OpenPermission(PlatformPermission permission) => PermissionPrefix + permission;
}

/// <summary>Notice topics: a newer notice on a topic replaces the shown one (see <c>NoticeQueue</c>).</summary>
internal static class NoticeTopics
{
    public const string Source = "source";
    public const string Update = Notifier.UpdateTopic;
    public const string Capture = "capture";
    public const string Tv = "tv";
    public const string SettingsSave = "settings-save";
    public const string Region = "region";
    public const string Library = "library";
    public const string Frame = "frame";
    public const string Pause = "pause";
    public const string Ai = "ai";
    public const string Hotkey = "hotkey";
    public const string Web = "web";

    public static string Permission(PlatformPermission p) => "permission:" + p;
}
