using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Translation.Ai;

namespace PSRemoteSub.Core.Translation;

/// <summary>
/// Runs the Google translator in the AI lane: sends only the text (no context, no brief) and turns Google's failures
/// into <see cref="AiTranslationException"/> so the lane treats them like an unavailable AI. One call at a time.
/// </summary>
public sealed class GoogleLaneTranslator(ITranslator inner) : IAiTranslator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> TranslateAsync(AiTranslationRequest request, CancellationToken ct)
    {
        if (request.SourceCode is not { } source || request.TargetCode is not { } target)
            throw new AiTranslationException(AiErrorKind.BadResponse, null);

        await _gate.WaitAsync(ct);
        try
        {
            return await inner.TranslateAsync(request.Text, SourceLanguages.GoogleCode(source),
                SourceLanguages.GoogleCode(target), ct);
        }
        catch (Exception e) when (e is TranslationUnavailableException or HttpRequestException
                                      || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new AiTranslationException(AiErrorKind.Unavailable, null);
        }
        finally { _gate.Release(); }
    }
}
