namespace PSRemoteSub.Core.Pipeline;

/// <param name="StaleFrameAfter">
/// How long without any new frame (while reading) before <c>FramesStalled</c> is reported. Windows.Graphics.Capture
/// only delivers a frame when the window content changes, so this is a backstop for a capture that died, not a
/// freshness check: a still scene legitimately produces no frames.
/// </param>
public sealed record PipelineOptions(TimeSpan OcrInterval, TimeSpan StaleFrameAfter, StabilityOptions Stability)
{
    /// <summary>OCR every 300 ms, frames stalled after 20 s without any frame, default stability thresholds.</summary>
    public static PipelineOptions Default { get; } =
        new(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(20), new StabilityOptions());
}
