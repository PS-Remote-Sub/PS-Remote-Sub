using PSRemoteSub.Core.Platform;
using PSRemoteSub.MacOS.Security;

namespace PSRemoteSub.MacOS.Tests;

public class MacPermissionsTests
{
    // Only Check: Camera/Microphone requests from an unbundled process (no usage strings) are killed by the OS, and
    // Screen Recording would show a prompt.
    [MacFact]
    public void Check_ReturnsAKnownStateForEveryMediaPermission()
    {
        var perms = new MacPermissions();
        foreach (var p in new[] { PlatformPermission.ScreenRecording, PlatformPermission.Camera, PlatformPermission.Microphone })
        {
            var state = perms.Check(p);
            Assert.NotEqual(PermissionState.Unknown, state);
        }
    }

    [MacFact]
    public void Check_ScreenRecording_IsGrantedOrDenied_NeverNotDetermined()
    {
        var state = new MacPermissions().Check(PlatformPermission.ScreenRecording);
        Assert.True(state is PermissionState.Granted or PermissionState.Denied);
    }

    [MacFact]
    public void Check_LocalNetwork_IsUnknown()
        => Assert.Equal(PermissionState.Unknown, new MacPermissions().Check(PlatformPermission.LocalNetwork));
}
