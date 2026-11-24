namespace PSRemoteSub.Core.Pipeline;

public sealed record StabilityOptions(
    double SameThreshold = 0.90,
    double DuplicateThreshold = 0.85,
    int RequiredReads = 2,
    TimeSpan? ClearAfter = null);
