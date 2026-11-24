using PSRemoteSub.MacOS.Capture;

namespace PSRemoteSub.MacOS.Tests;

public class CaptureRetryPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(3);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void Healthy_NeverActs()
    {
        var policy = new CaptureRetryPolicy(Backoff);
        Assert.Equal(CaptureRetryAction.None, policy.Observe(CaptureStatus.NoFrame, At(0)));
        Assert.Equal(CaptureRetryAction.None, policy.Observe(CaptureStatus.Frame, At(1)));
        Assert.Equal(CaptureRetryAction.None, policy.Observe(CaptureStatus.Frame, At(100)));
        Assert.False(policy.Failing);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Failure_LoggedOnce_ThenRestartsEveryBackoff(bool windowNotFound)
    {
        var failure = windowNotFound ? CaptureStatus.WindowNotFound : CaptureStatus.Failed;
        var policy = new CaptureRetryPolicy(Backoff);
        Assert.Equal(CaptureRetryAction.FirstFailure, policy.Observe(failure, At(0)));
        Assert.True(policy.Failing);
        Assert.Equal(CaptureRetryAction.None, policy.Observe(failure, At(1)));
        Assert.Equal(CaptureRetryAction.None, policy.Observe(failure, At(2.9)));
        Assert.Equal(CaptureRetryAction.Restart, policy.Observe(failure, At(3)));
        // The restarted stream is starting: no frame yet is not a new failure and not a recovery.
        Assert.Equal(CaptureRetryAction.None, policy.Observe(CaptureStatus.NoFrame, At(3.5)));
        Assert.Equal(CaptureRetryAction.None, policy.Observe(failure, At(4)));
        Assert.Equal(CaptureRetryAction.None, policy.Observe(failure, At(5.9)));
        Assert.Equal(CaptureRetryAction.Restart, policy.Observe(failure, At(6)));
        Assert.Equal(CaptureRetryAction.Restart, policy.Observe(failure, At(9.5)));
    }

    [Fact]
    public void FailureKindChange_IsStillOneFailure()
    {
        var policy = new CaptureRetryPolicy(Backoff);
        Assert.Equal(CaptureRetryAction.FirstFailure, policy.Observe(CaptureStatus.WindowNotFound, At(0)));
        Assert.Equal(CaptureRetryAction.None, policy.Observe(CaptureStatus.Failed, At(1)));
        Assert.Equal(CaptureRetryAction.Restart, policy.Observe(CaptureStatus.WindowNotFound, At(3)));
    }

    [Fact]
    public void FrameAfterFailure_RecoveredOnce_ThenNewFailureLogsAgain()
    {
        var policy = new CaptureRetryPolicy(Backoff);
        policy.Observe(CaptureStatus.Failed, At(0));
        policy.Observe(CaptureStatus.Failed, At(3));
        Assert.Equal(CaptureRetryAction.Recovered, policy.Observe(CaptureStatus.Frame, At(4)));
        Assert.False(policy.Failing);
        Assert.Equal(CaptureRetryAction.None, policy.Observe(CaptureStatus.Frame, At(5)));
        Assert.Equal(CaptureRetryAction.FirstFailure, policy.Observe(CaptureStatus.Failed, At(6)));
        Assert.Equal(CaptureRetryAction.None, policy.Observe(CaptureStatus.Failed, At(8)));
        Assert.Equal(CaptureRetryAction.Restart, policy.Observe(CaptureStatus.Failed, At(9)));
    }

    [Fact]
    public void NoPermission_NeverRestarts()
    {
        var policy = new CaptureRetryPolicy(Backoff);
        for (int i = 0; i < 100; i++)
            Assert.Equal(CaptureRetryAction.None, policy.Observe(CaptureStatus.NoPermission, At(i * 10)));
        Assert.False(policy.Failing);
    }

    [Fact]
    public void Reset_ForgetsFailure()
    {
        var policy = new CaptureRetryPolicy(Backoff);
        policy.Observe(CaptureStatus.Failed, At(0));
        policy.Reset();
        Assert.False(policy.Failing);
        Assert.Equal(CaptureRetryAction.FirstFailure, policy.Observe(CaptureStatus.Failed, At(1)));
    }

    [Fact]
    public void ClockGoingBackwards_DoesNotStallRetries()
    {
        var policy = new CaptureRetryPolicy(Backoff);
        policy.Observe(CaptureStatus.Failed, At(10));
        Assert.Equal(CaptureRetryAction.None, policy.Observe(CaptureStatus.Failed, At(5)));
        Assert.Equal(CaptureRetryAction.Restart, policy.Observe(CaptureStatus.Failed, At(8)));
    }
}
