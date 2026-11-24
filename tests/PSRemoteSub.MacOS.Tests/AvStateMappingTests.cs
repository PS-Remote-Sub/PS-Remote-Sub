using PSRemoteSub.Core.Capture;
using PSRemoteSub.MacOS.Capture;

namespace PSRemoteSub.MacOS.Tests;

public class AvStateMappingTests
{
    [Fact] public void Opening_IsOpening() => Assert.Equal(CaptureState.Opening, AvStateMapping.ToState(AvEvent.Opening));

    [Fact] public void Running_IsPlaying() => Assert.Equal(CaptureState.Playing, AvStateMapping.ToState(AvEvent.Running));

    [Fact] public void NoFrames3s_IsNoSignal() => Assert.Equal(CaptureState.NoSignal, AvStateMapping.ToState(AvEvent.NoFrames3s));

    [Fact] public void DeviceInUse_IsInUse() => Assert.Equal(CaptureState.InUse, AvStateMapping.ToState(AvEvent.DeviceInUse));

    [Fact] public void NotAuthorized_IsBlocked() => Assert.Equal(CaptureState.Blocked, AvStateMapping.ToState(AvEvent.NotAuthorized));

    [Fact] public void Disconnected_IsUnplugged() => Assert.Equal(CaptureState.Unplugged, AvStateMapping.ToState(AvEvent.Disconnected));

    [Fact] public void RuntimeError_IsFailed() => Assert.Equal(CaptureState.Failed, AvStateMapping.ToState(AvEvent.RuntimeError));

    [Fact] public void DeviceInUseError_IsInUse() => Assert.Equal(CaptureState.InUse, AvStateMapping.ToState(AvEvent.DeviceInUseError));

    // The native event values (CaptureCard.swift) are part of the contract.
    [Fact] public void DeviceInUseError_NativeValue7() => Assert.Equal(7, (int)AvEvent.DeviceInUseError);

    [Theory]
    [InlineData(nameof(AvEvent.Disconnected))]
    [InlineData(nameof(AvEvent.RuntimeError))]
    [InlineData(nameof(AvEvent.NotAuthorized))]
    [InlineData(nameof(AvEvent.DeviceInUseError))] // in use when opening, or a runtime error saying so: the session is dead
    public void Reopens_AfterAFinalEvent(string e) => Assert.True(AvStateMapping.Reopens(Enum.Parse<AvEvent>(e)));

    [Theory]
    [InlineData(nameof(AvEvent.Opening))]
    [InlineData(nameof(AvEvent.Running))]
    [InlineData(nameof(AvEvent.NoFrames3s))]
    [InlineData(nameof(AvEvent.DeviceInUse))] // an interruption: AVFoundation resumes the session itself when it ends
    public void Reopens_NotWhileTheSessionLives(string e) => Assert.False(AvStateMapping.Reopens(Enum.Parse<AvEvent>(e)));
}
