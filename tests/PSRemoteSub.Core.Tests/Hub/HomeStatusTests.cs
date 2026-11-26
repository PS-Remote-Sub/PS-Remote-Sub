using PSRemoteSub.Core.Hub;

namespace PSRemoteSub.Core.Tests.Hub;

public sealed class HomeStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 21, 30, 0, TimeSpan.Zero);
    private static readonly PipelineStatus Good = new(RemotePlayFound: true, FramesStalled: false, Paused: false, NoRegion: false);

    [Fact]
    public void AllGood_Translating()
    {
        var s = HomeStatus.Summarize(Good, paused: false, Now);
        Assert.Equal(HomeState.Translating, s.State);
        Assert.Empty(s.Issues);
    }

    [Fact]
    public void Paused_HidesIssues()
    {
        var s = HomeStatus.Summarize(Good with { RemotePlayFound = false }, paused: true, Now);
        Assert.Equal(HomeState.Paused, s.State);
        Assert.Empty(s.Issues);
    }

    [Fact]
    public void StatusPaused_AlsoPaused() =>
        Assert.Equal(HomeState.Paused, HomeStatus.Summarize(Good with { Paused = true }, paused: false, Now).State);

    [Fact]
    public void Order_SourceThenOcrThenRegionThenAi()
    {
        var status = Good with { RemotePlayFound = false, OcrUnavailable = true, NoRegion = true, AiError = "AiAuth" };
        var s = HomeStatus.Summarize(status, paused: false, Now);
        Assert.Equal(HomeState.NeedsFix, s.State);
        Assert.Equal([HomeIssue.NoSource, HomeIssue.OcrUnavailable, HomeIssue.NoRegion, HomeIssue.AiError], s.Issues);
    }

    [Fact]
    public void Minimized_ReplacesStalled()
    {
        var s = HomeStatus.Summarize(Good with { Minimized = true, FramesStalled = true }, paused: false, Now);
        Assert.Equal([HomeIssue.Minimized], s.Issues);
    }

    [Fact]
    public void Stalled_WhenNotMinimized() =>
        Assert.Equal([HomeIssue.FramesStalled], HomeStatus.Summarize(Good with { FramesStalled = true }, false, Now).Issues);

    [Fact]
    public void AiError_PastRetry_Ignored()
    {
        var s = HomeStatus.Summarize(Good with { AiError = "AiRateLimited", AiRetryAt = Now.AddSeconds(-1) }, false, Now);
        Assert.Equal(HomeState.Translating, s.State);
    }

    [Fact]
    public void AiError_FutureRetry_Kept()
    {
        var s = HomeStatus.Summarize(Good with { AiError = "AiRateLimited", AiRetryAt = Now.AddSeconds(10) }, false, Now);
        Assert.Equal([HomeIssue.AiError], s.Issues);
    }

    [Fact]
    public void AutoOpen_NotForMissingRemotePlayOnly()
    {
        Assert.False(HomeStatus.ShouldAutoOpen(HomeStatus.Summarize(Good with { RemotePlayFound = false }, false, Now)));
        Assert.False(HomeStatus.ShouldAutoOpen(HomeStatus.Summarize(Good with { FramesStalled = true }, false, Now)));
    }

    [Fact]
    public void AutoOpen_ForOcrOrRegionOrAi()
    {
        Assert.True(HomeStatus.ShouldAutoOpen(HomeStatus.Summarize(Good with { RemotePlayFound = false, NoRegion = true }, false, Now)));
        Assert.True(HomeStatus.ShouldAutoOpen(HomeStatus.Summarize(Good with { OcrUnavailable = true }, false, Now)));
        Assert.True(HomeStatus.ShouldAutoOpen(HomeStatus.Summarize(Good with { AiError = "AiAuth" }, false, Now)));
    }

    [Fact]
    public void AutoOpen_FalseWhenTranslatingOrPaused()
    {
        Assert.False(HomeStatus.ShouldAutoOpen(HomeStatus.Summarize(Good, false, Now)));
        Assert.False(HomeStatus.ShouldAutoOpen(HomeStatus.Summarize(Good with { NoRegion = true }, paused: true, Now)));
    }
}
