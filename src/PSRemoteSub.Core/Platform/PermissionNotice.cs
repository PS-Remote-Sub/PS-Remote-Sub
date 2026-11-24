namespace PSRemoteSub.Core.Platform;

/// <summary>
/// Tracks which of the permissions the app needs are missing (denied, or not asked yet) and says when to tell the user:
/// once per permission per time it goes missing, so a grant followed by a loss (macOS may drop an unsigned app's
/// permissions after an update) is announced again, but a permission that stays missing is never nagged about.
/// <see cref="PermissionState.Unknown"/> (the OS cannot tell) is not missing. UI thread only.
/// </summary>
public sealed class PermissionNotice
{
    private readonly IPermissions _permissions;
    private readonly HashSet<PlatformPermission> _notified = [];
    private IReadOnlyList<PlatformPermission> _missing = [];

    public PermissionNotice(IPermissions permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        _permissions = permissions;
    }

    /// <summary>The needed permissions that are missing as of the last <see cref="Refresh"/>, in the order asked for.</summary>
    public IReadOnlyList<PlatformPermission> Missing => _missing;

    /// <summary>Re-checks the needed permissions. True when <see cref="Missing"/> changed.</summary>
    public bool Refresh(IEnumerable<PlatformPermission> needed)
    {
        ArgumentNullException.ThrowIfNull(needed);
        var now = new List<PlatformPermission>();
        foreach (var p in needed.Distinct())
        {
            if (_permissions.Check(p) is PermissionState.Denied or PermissionState.NotDetermined)
                now.Add(p);
        }
        _notified.IntersectWith(now); // a granted (or no longer needed) permission may be announced again if it is lost
        bool changed = !now.SequenceEqual(_missing);
        _missing = now;
        return changed;
    }

    /// <summary>True the first time a missing permission is asked about since it went missing; false after, and when it is not missing.</summary>
    public bool ShouldNotify(PlatformPermission p) => _missing.Contains(p) && _notified.Add(p);

    /// <summary>
    /// The permissions granted between two <see cref="Refresh"/>es: missing before, not missing after, and still needed (one
    /// that dropped out only because it is no longer needed was not granted). Empty where nothing is ever missing (Windows).
    /// </summary>
    public static IReadOnlyList<PlatformPermission> NewlyGranted(IEnumerable<PlatformPermission> missingBefore,
        IEnumerable<PlatformPermission> missingAfter, IEnumerable<PlatformPermission> needed)
    {
        ArgumentNullException.ThrowIfNull(missingBefore);
        ArgumentNullException.ThrowIfNull(missingAfter);
        ArgumentNullException.ThrowIfNull(needed);
        var after = missingAfter.ToHashSet();
        var stillNeeded = needed.ToHashSet();
        return [.. missingBefore.Distinct().Where(p => !after.Contains(p) && stillNeeded.Contains(p))];
    }
}
