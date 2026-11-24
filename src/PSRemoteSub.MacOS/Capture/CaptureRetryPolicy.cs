namespace PSRemoteSub.MacOS.Capture;

/// <summary>What the native ScreenCaptureKit stream reports (psrs_sck_copy_latest), as the retry policy sees it.</summary>
internal enum CaptureStatus
{
    /// <summary>At least one frame has arrived.</summary>
    Frame,
    /// <summary>Starting, or running with no frame yet (also a minimised window).</summary>
    NoFrame,
    /// <summary>Screen Recording is not granted: capture was never attempted.</summary>
    NoPermission,
    /// <summary>ScreenCaptureKit does not (yet) list the window.</summary>
    WindowNotFound,
    /// <summary>The stream could not start, or stopped with an error (sleep/wake, display change, Stop in the menu bar).</summary>
    Failed,
}

internal enum CaptureRetryAction
{
    None,
    /// <summary>The capture just started failing: log it once.</summary>
    FirstFailure,
    /// <summary>Still failing and the backoff has passed: start the native capture again (no log).</summary>
    Restart,
    /// <summary>A frame arrived after a failure: log the recovery once.</summary>
    Recovered,
}

/// <summary>
/// When to restart a failed window capture. On Windows a failed start throws and the pipeline retries every tick; on
/// Mac the start is asynchronous and always "succeeds", so the frame source itself restarts a capture that failed
/// (window not found / stream failed) once per <c>backoff</c> while the window stays the same. Missing permission is
/// never retried, so the app cannot loop on it. Not thread-safe (the frame source calls it under its lock).
/// </summary>
internal sealed class CaptureRetryPolicy(TimeSpan backoff)
{
    private DateTimeOffset _lastAttempt;

    /// <summary>True from the first failure until a frame arrives (or <see cref="Reset"/>).</summary>
    public bool Failing { get; private set; }

    /// <summary>Forgets any failure (a new capture was started).</summary>
    public void Reset() => Failing = false;

    public CaptureRetryAction Observe(CaptureStatus status, DateTimeOffset now)
    {
        switch (status)
        {
            case CaptureStatus.Frame:
                if (!Failing)
                    return CaptureRetryAction.None;
                Failing = false;
                return CaptureRetryAction.Recovered;

            case CaptureStatus.WindowNotFound or CaptureStatus.Failed:
                if (!Failing)
                {
                    Failing = true;
                    _lastAttempt = now;
                    return CaptureRetryAction.FirstFailure;
                }
                if (now < _lastAttempt)
                {
                    // The clock went backwards: count the backoff from now, so retries never stall.
                    _lastAttempt = now;
                    return CaptureRetryAction.None;
                }
                if (now - _lastAttempt < backoff)
                    return CaptureRetryAction.None;
                _lastAttempt = now;
                return CaptureRetryAction.Restart;

            default:
                return CaptureRetryAction.None;
        }
    }
}
