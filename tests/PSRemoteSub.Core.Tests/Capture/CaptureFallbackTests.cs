using PSRemoteSub.Core.Capture;

namespace PSRemoteSub.Core.Tests.Capture;

public class CaptureFallbackTests
{
    private const long Silent = CaptureFallback.SilentAfterMs;

    [Fact]
    public void Preferred_SilentBriefly_KeepsReading() =>
        Assert.Equal(CaptureAttemptStep.KeepReading,
            new CaptureFallback().Next(onFallback: false, fallbackAvailable: true, readingMs: Silent - 1, hadFrame: false));

    [Fact]
    public void Preferred_Silent3s_FallsBack() =>
        Assert.Equal(CaptureAttemptStep.FallBack,
            new CaptureFallback().Next(onFallback: false, fallbackAvailable: true, readingMs: Silent, hadFrame: false));

    [Fact]
    public void Preferred_Silent3s_NoFallback_KeepsReading() =>
        Assert.Equal(CaptureAttemptStep.KeepReading,
            new CaptureFallback().Next(onFallback: false, fallbackAvailable: false, readingMs: Silent * 10, hadFrame: false));

    [Fact]
    public void Preferred_Frames_KeepsReading() =>
        Assert.Equal(CaptureAttemptStep.KeepReading,
            new CaptureFallback().Next(onFallback: false, fallbackAvailable: true, readingMs: Silent * 10, hadFrame: true));

    [Fact]
    public void Fallback_FramesAtOnce_Sticks()
    {
        // The preferred mode sent nothing while the fallback reads at once: the preferred mode does not work here.
        var fallback = new CaptureFallback();
        Assert.Equal(CaptureAttemptStep.KeepReading, fallback.Next(true, false, 100, hadFrame: true));
        Assert.Equal(CaptureAttemptStep.KeepReading, fallback.Next(true, false, Silent * 100, hadFrame: true));
    }

    [Fact]
    public void Fallback_SilentToo_ThenFrames_RetriesPreferredOnce()
    {
        // No signal: neither format sends anything. When the signal comes, the preferred mode is tried again.
        var fallback = new CaptureFallback();
        Assert.Equal(CaptureAttemptStep.FallBack, fallback.Next(false, true, Silent, hadFrame: false));
        Assert.Equal(CaptureAttemptStep.KeepReading, fallback.Next(true, false, Silent, hadFrame: false));
        Assert.Equal(CaptureAttemptStep.KeepReading, fallback.Next(true, false, Silent * 20, hadFrame: false));
        Assert.Equal(CaptureAttemptStep.RetryPreferred, fallback.Next(true, false, Silent * 21, hadFrame: true));

        // The preferred mode now reads: it stays.
        Assert.Equal(CaptureAttemptStep.KeepReading, fallback.Next(false, true, 200, hadFrame: true));
    }

    [Fact]
    public void Fallback_SilentToo_PreferredStillSilentWithSignal_SticksToFallback()
    {
        var fallback = new CaptureFallback();
        fallback.Next(false, true, Silent, hadFrame: false);          // falls back
        fallback.Next(true, false, Silent, hadFrame: false);          // silent too: no signal
        Assert.Equal(CaptureAttemptStep.RetryPreferred, fallback.Next(true, false, Silent * 5, hadFrame: true));

        // With the signal there, the preferred mode still sends nothing: it falls back, and this time stays there.
        Assert.Equal(CaptureAttemptStep.FallBack, fallback.Next(false, true, Silent, hadFrame: false));
        Assert.Equal(CaptureAttemptStep.KeepReading, fallback.Next(true, false, 100, hadFrame: true));
        Assert.Equal(CaptureAttemptStep.KeepReading, fallback.Next(true, false, Silent * 50, hadFrame: true));
    }

    [Fact]
    public void Fallback_Silent_SurvivesAFailedAttempt()
    {
        // A fallback attempt that failed and was reopened is still unproven until it gets frames.
        var fallback = new CaptureFallback();
        fallback.Next(false, true, Silent, hadFrame: false);
        fallback.Next(true, false, Silent, hadFrame: false);
        // (the attempt fails and the fallback mode is reopened: a new attempt, readingMs from 0)
        Assert.Equal(CaptureAttemptStep.RetryPreferred, fallback.Next(true, false, 50, hadFrame: true));
    }
}
