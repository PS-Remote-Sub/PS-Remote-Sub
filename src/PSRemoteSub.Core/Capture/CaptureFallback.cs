namespace PSRemoteSub.Core.Capture;

/// <summary>What a running capture attempt should do next (see <see cref="CaptureFallback"/>).</summary>
public enum CaptureAttemptStep
{
    /// <summary>Go on reading this mode.</summary>
    KeepReading,

    /// <summary>The uncompressed mode sent nothing: reopen as its MJPG fallback.</summary>
    FallBack,

    /// <summary>The fallback was chosen during no signal: now that frames arrive, reopen the preferred mode.</summary>
    RetryPreferred,
}

/// <summary>
/// Decides when a capture session switches between the mode it prefers and that mode's MJPG fallback. An uncompressed
/// mode that sends nothing for <see cref="SilentAfterMs"/> falls back. But some cards send nothing in any format without
/// a signal: when the fallback is silent too, the silence was the signal's, not the mode's, so once frames arrive the
/// preferred mode is tried again instead of decoding MJPG on the CPU for the rest of the session. A fallback that reads
/// at once (the preferred mode really does not work) is kept. One instance per session; not thread-safe.
/// </summary>
public sealed class CaptureFallback
{
    /// <summary>How long a mode may send nothing before it counts as silent.</summary>
    public const long SilentAfterMs = 3000;

    private bool _unproven;  // the fallback was silent too: the preferred mode was never tried with a signal

    /// <summary>
    /// Called on each watch tick of an attempt.
    /// </summary>
    /// <param name="onFallback">The attempt reads the MJPG fallback.</param>
    /// <param name="fallbackAvailable">The attempt reads the preferred mode and it has an MJPG fallback.</param>
    /// <param name="readingMs">Time since the attempt started reading.</param>
    /// <param name="hadFrame">The attempt has received a frame.</param>
    public CaptureAttemptStep Next(bool onFallback, bool fallbackAvailable, long readingMs, bool hadFrame)
    {
        if (!hadFrame)
        {
            if (readingMs < SilentAfterMs)
                return CaptureAttemptStep.KeepReading;
            if (onFallback)
            {
                _unproven = true;
                return CaptureAttemptStep.KeepReading; // no signal: wait for one on this mode
            }
            return fallbackAvailable ? CaptureAttemptStep.FallBack : CaptureAttemptStep.KeepReading;
        }
        if (onFallback && _unproven)
        {
            _unproven = false;
            return CaptureAttemptStep.RetryPreferred;
        }
        return CaptureAttemptStep.KeepReading;
    }
}
