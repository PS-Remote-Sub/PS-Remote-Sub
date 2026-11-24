namespace PSRemoteSub.Core.Tv;

/// <summary>
/// The colon-separated <c>enabled_accessibility_services</c> secure setting. Other apps' services (TalkBack...) must
/// survive, so ours is added to the list, never written over it.
/// </summary>
public static class AccessibilityList
{
    public const string Ours = "th.psrs.tvoverlay/.OverlayA11yService";

    /// <summary>The list with our service included once. <c>null</c> / "null" (what adb prints when unset) / empty count as an empty list.</summary>
    public static string Add(string? current)
    {
        var entries = Entries(current);
        if (!entries.Contains(Ours, StringComparer.OrdinalIgnoreCase))
            entries.Add(Ours);
        return string.Join(':', entries);
    }

    /// <summary>Whether our service is one of the entries (whole-entry, case-insensitive comparison).</summary>
    public static bool Contains(string? current) => Entries(current).Contains(Ours, StringComparer.OrdinalIgnoreCase);

    private static List<string> Entries(string? current)
    {
        var trimmed = current?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Equals("null", StringComparison.OrdinalIgnoreCase))
            return [];
        return trimmed.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
