using PSRemoteSub.Core.Capture;

namespace PSRemoteSub.Core.Tests.Capture;

public class CaptureWindowRulesTests
{
    [Theory]
    [InlineData(CaptureState.NoDevice, true)]
    [InlineData(CaptureState.Unplugged, true)]
    [InlineData(CaptureState.Opening, false)]
    [InlineData(CaptureState.Playing, false)]
    [InlineData(CaptureState.NoSignal, false)]
    [InlineData(CaptureState.InUse, false)]
    [InlineData(CaptureState.Blocked, false)]
    [InlineData(CaptureState.Failed, false)]
    public void ShouldLookForDevice_OnlyWhileTheCardIsMissing(CaptureState state, bool expected) =>
        Assert.Equal(expected, CaptureWindowRules.ShouldLookForDevice(state));

    [Fact]
    public void ShouldRestartAudio_BackToPlaying_AudioLost_Restarts() =>
        Assert.True(CaptureWindowRules.ShouldRestartAudio(
            CaptureState.Unplugged, CaptureState.Playing, audioRunning: false, audioStartPending: false));

    [Fact]
    public void ShouldRestartAudio_AudioRunning_No() =>
        Assert.False(CaptureWindowRules.ShouldRestartAudio(
            CaptureState.Unplugged, CaptureState.Playing, audioRunning: true, audioStartPending: false));

    [Fact]
    public void ShouldRestartAudio_StartStillPending_No() =>
        // The first Opening → Playing: the audio start queued with the open has not finished yet.
        Assert.False(CaptureWindowRules.ShouldRestartAudio(
            CaptureState.Opening, CaptureState.Playing, audioRunning: false, audioStartPending: true));

    [Fact]
    public void ShouldRestartAudio_StillPlaying_No() =>
        Assert.False(CaptureWindowRules.ShouldRestartAudio(
            CaptureState.Playing, CaptureState.Playing, audioRunning: false, audioStartPending: false));

    [Theory]
    [InlineData(CaptureState.NoSignal)]
    [InlineData(CaptureState.Unplugged)]
    [InlineData(CaptureState.Failed)]
    public void ShouldRestartAudio_NotPlaying_No(CaptureState current) =>
        Assert.False(CaptureWindowRules.ShouldRestartAudio(
            CaptureState.Playing, current, audioRunning: false, audioStartPending: false));

    [Fact]
    public void KeepsDisplayAwake_OpenPlayingShown_Yes() =>
        Assert.True(CaptureWindowRules.KeepsDisplayAwake(windowOpen: true, windowMinimized: false, CaptureState.Playing));

    [Fact]
    public void KeepsDisplayAwake_Minimized_No() =>
        Assert.False(CaptureWindowRules.KeepsDisplayAwake(windowOpen: true, windowMinimized: true, CaptureState.Playing));

    [Fact]
    public void KeepsDisplayAwake_Closed_No() =>
        Assert.False(CaptureWindowRules.KeepsDisplayAwake(windowOpen: false, windowMinimized: false, CaptureState.Playing));

    [Theory]
    [InlineData(CaptureState.NoSignal)]
    [InlineData(CaptureState.Opening)]
    [InlineData(CaptureState.NoDevice)]
    [InlineData(null)]
    public void KeepsDisplayAwake_NotPlaying_No(CaptureState? state) =>
        Assert.False(CaptureWindowRules.KeepsDisplayAwake(windowOpen: true, windowMinimized: false, state));
}
