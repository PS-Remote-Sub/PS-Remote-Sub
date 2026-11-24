namespace PSRemoteSub.Core.Translation;

public interface ITranslator
{
    Task<string> TranslateAsync(string text, string sourceGoogle, string targetGoogle, CancellationToken ct);
}

/// <summary>Thrown while both Google endpoints are failing; <see cref="RetryAt"/> is the end of the pause.</summary>
public sealed class TranslationUnavailableException(string reason, DateTimeOffset retryAt) : Exception(reason)
{
    public DateTimeOffset RetryAt { get; } = retryAt;
}
