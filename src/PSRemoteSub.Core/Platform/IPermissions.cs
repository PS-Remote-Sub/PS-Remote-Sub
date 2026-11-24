namespace PSRemoteSub.Core.Platform;

/// <summary>An OS permission the app may need.</summary>
public enum PlatformPermission
{
    ScreenRecording,
    Camera,
    Microphone,
    LocalNetwork,
}

/// <summary>Where an OS permission stands. <see cref="Unknown"/> when the OS offers no way to tell.</summary>
public enum PermissionState
{
    Granted,
    NotDetermined,
    Denied,
    Unknown,
}

/// <summary>Checks, requests and shows the settings page of OS permissions (a no-op where the OS has none).</summary>
public interface IPermissions
{
    PermissionState Check(PlatformPermission p);

    /// <summary>Asks the OS for the permission; true when it ends up granted.</summary>
    Task<bool> RequestAsync(PlatformPermission p);

    /// <summary>Opens the OS settings page where the user can change the permission.</summary>
    void OpenSettings(PlatformPermission p);
}
