using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.Core.Tests.Platform;

public class PermissionNoticeTests
{
    private sealed class FakePermissions : IPermissions
    {
        public Dictionary<PlatformPermission, PermissionState> States { get; } = new();

        public PermissionState Check(PlatformPermission p) => States.GetValueOrDefault(p, PermissionState.Granted);

        public Task<bool> RequestAsync(PlatformPermission p) => Task.FromResult(false);

        public void OpenSettings(PlatformPermission p) { }
    }

    private static readonly PlatformPermission[] Screen = [PlatformPermission.ScreenRecording];

    [Fact]
    public void Refresh_Denied_AddsMissing_ReturnsTrue()
    {
        var fake = new FakePermissions();
        fake.States[PlatformPermission.ScreenRecording] = PermissionState.Denied;
        var notice = new PermissionNotice(fake);

        Assert.True(notice.Refresh(Screen));
        Assert.Equal(Screen, notice.Missing);
    }

    [Fact]
    public void Refresh_Same_ReturnsFalse()
    {
        var fake = new FakePermissions();
        fake.States[PlatformPermission.ScreenRecording] = PermissionState.Denied;
        var notice = new PermissionNotice(fake);
        notice.Refresh(Screen);

        Assert.False(notice.Refresh(Screen));
    }

    [Fact]
    public void ShouldNotify_OncePerLoss()
    {
        var fake = new FakePermissions();
        fake.States[PlatformPermission.ScreenRecording] = PermissionState.Denied;
        var notice = new PermissionNotice(fake);
        notice.Refresh(Screen);

        Assert.True(notice.ShouldNotify(PlatformPermission.ScreenRecording));
        Assert.False(notice.ShouldNotify(PlatformPermission.ScreenRecording));
        notice.Refresh(Screen);
        Assert.False(notice.ShouldNotify(PlatformPermission.ScreenRecording));
    }

    [Fact]
    public void ShouldNotify_AgainAfterGrantThenLoss()
    {
        var fake = new FakePermissions();
        fake.States[PlatformPermission.ScreenRecording] = PermissionState.Denied;
        var notice = new PermissionNotice(fake);
        notice.Refresh(Screen);
        Assert.True(notice.ShouldNotify(PlatformPermission.ScreenRecording));

        fake.States[PlatformPermission.ScreenRecording] = PermissionState.Granted;
        Assert.True(notice.Refresh(Screen));
        Assert.Empty(notice.Missing);

        fake.States[PlatformPermission.ScreenRecording] = PermissionState.Denied;
        notice.Refresh(Screen);
        Assert.True(notice.ShouldNotify(PlatformPermission.ScreenRecording));
    }

    [Fact]
    public void Unknown_IsNotMissing()
    {
        var fake = new FakePermissions();
        fake.States[PlatformPermission.ScreenRecording] = PermissionState.Unknown;
        var notice = new PermissionNotice(fake);

        Assert.False(notice.Refresh(Screen));
        Assert.Empty(notice.Missing);
        Assert.False(notice.ShouldNotify(PlatformPermission.ScreenRecording));
    }

    [Fact]
    public void Refresh_NotNeededAnyMore_DropsFromMissing()
    {
        var fake = new FakePermissions();
        fake.States[PlatformPermission.ScreenRecording] = PermissionState.Denied;
        var notice = new PermissionNotice(fake);
        notice.Refresh(Screen);

        Assert.True(notice.Refresh([]));
        Assert.Empty(notice.Missing);
    }

    // ---- NewlyGranted ----

    private static readonly PlatformPermission[] Card = [PlatformPermission.Camera, PlatformPermission.Microphone];

    [Fact]
    public void NewlyGranted_MicrophoneMissingThenGranted_ReturnsIt() =>
        Assert.Equal([PlatformPermission.Microphone],
            PermissionNotice.NewlyGranted([PlatformPermission.Microphone], [], Card));

    [Fact]
    public void NewlyGranted_BothGranted_ReturnsBoth() =>
        Assert.Equal(Card, PermissionNotice.NewlyGranted(Card, [], Card));

    [Fact]
    public void NewlyGranted_StillMissing_Empty() =>
        Assert.Empty(PermissionNotice.NewlyGranted([PlatformPermission.Camera], [PlatformPermission.Camera], Card));

    [Fact]
    public void NewlyGranted_NothingWasMissing_Empty() =>
        Assert.Empty(PermissionNotice.NewlyGranted([], [], Card)); // Windows: nothing is ever missing

    [Fact]
    public void NewlyGranted_NewlyLost_Empty() =>
        Assert.Empty(PermissionNotice.NewlyGranted([], [PlatformPermission.Microphone], Card));

    [Fact]
    public void NewlyGranted_NoLongerNeeded_IsNotAGrant() =>
        // Switching from the card to Remote Play drops Camera and Microphone from the missing list without a grant.
        Assert.Empty(PermissionNotice.NewlyGranted(Card, [PlatformPermission.ScreenRecording], Screen));
}
