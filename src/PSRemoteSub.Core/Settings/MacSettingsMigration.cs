namespace PSRemoteSub.Core.Settings;

/// <summary>
/// One-time settings fix-ups for macOS. Has no OS check of its own: the app only calls it when running on macOS.
/// </summary>
public static class MacSettingsMigration
{
    /// <summary>The web port a Mac install uses by default.</summary>
    public const int MacDefaultWebPort = 5055;

    /// <summary>The port macOS reserves for AirPlay Receiver, so the web server cannot bind it.</summary>
    public const int BlockedPort = 5000;

    /// <summary>Moves the blocked default port to the Mac default. Returns true when it changed something.</summary>
    public static bool Apply(AppSettings s)
    {
        if (s.WebPort != BlockedPort)
            return false;
        s.WebPort = MacDefaultWebPort;
        return true;
    }
}
