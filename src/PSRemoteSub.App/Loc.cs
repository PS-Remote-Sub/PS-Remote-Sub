using System.Globalization;
using System.Resources;

namespace PSRemoteSub.App;

/// <summary>UI copy from <c>Strings.resx</c> (neutral resources are Thai).</summary>
internal static class Loc
{
    private static readonly ResourceManager Resources = new("PSRemoteSub.App.Strings", typeof(Loc).Assembly);

    /// <summary>
    /// The string for <paramref name="key"/>, or the key itself when it is missing. On macOS a <c>key + "Mac"</c> entry,
    /// when there is one, replaces it (Windows-specific wording: taskbar, Ctrl, Windows dialogs) — the one place that picks.
    /// </summary>
    public static string Get(string key) => ForPlatform(key, OperatingSystem.IsMacOS());

    /// <summary>The Mac variant (<c>key + "Mac"</c>) when <paramref name="mac"/> and it exists, otherwise <paramref name="key"/>'s text.</summary>
    public static string ForPlatform(string key, bool mac) =>
        (mac ? Resources.GetString(key + "Mac", CultureInfo.CurrentUICulture) : null)
        ?? Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
