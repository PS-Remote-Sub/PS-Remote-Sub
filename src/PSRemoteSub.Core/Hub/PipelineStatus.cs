namespace PSRemoteSub.Core.Hub;

/// <param name="Minimized">The Remote Play window is minimized, so nothing can be read until it is restored.</param>
/// <param name="OcrUnavailable">The OCR language pack for the active source language is not installed.</param>
/// <param name="AiError">AI error kind or config problem (resource key, for example <c>AiDown</c>) or null; UI layers localize it.</param>
/// <param name="AiRetryAt">End of the AI translator's backoff, when known.</param>
public sealed record PipelineStatus(
    bool RemotePlayFound,
    bool FramesStalled,
    bool Paused,
    bool NoRegion,
    bool Minimized = false,
    bool OcrUnavailable = false,
    string? AiError = null,
    DateTimeOffset? AiRetryAt = null)
{
    /// <summary>Before the pipeline's first tick: nothing found, nothing wrong yet.</summary>
    public static PipelineStatus Initial { get; } = new(false, false, false, false);
}
