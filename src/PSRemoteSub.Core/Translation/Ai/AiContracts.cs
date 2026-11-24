using PSRemoteSub.Core.Library;

namespace PSRemoteSub.Core.Translation.Ai;

public interface IAiTranslator
{
    Task<string> TranslateAsync(AiTranslationRequest request, CancellationToken ct);
}

public sealed record AiContextLine(string Source, string? Translation);

/// <param name="SourceCode">App language code of the source (e.g. <c>zh</c>); only the Google lane needs it.</param>
/// <param name="TargetCode">App language code of the target (e.g. <c>th</c>); only the Google lane needs it.</param>
public sealed record AiTranslationRequest(
    string Text,
    IReadOnlyList<AiContextLine> Context,
    string SourceLanguageName,
    string TargetLanguageName,
    string? GameTitle,
    AiRequestKind Kind = AiRequestKind.Subtitle,
    GameBrief? Brief = null,
    string? SourceCode = null,
    string? TargetCode = null);

/// <summary><c>Subtitle</c>: translate a subtitle line. <c>Names</c>: transliterate character names, one per line.</summary>
public enum AiRequestKind { Subtitle, Names }

public sealed record AiEndpoint(Uri BaseUrl, string? ApiKey, string Model);

public enum AiErrorKind { Auth, RateLimited, Unavailable, BadResponse, ModelNotFound, Billing }

/// <summary>An AI call failed. The message is only the kind and HTTP status: never a body, key or subtitle text.</summary>
/// <param name="providerMessage">
/// The provider's own error text (<c>error.message</c>), one line, at most 300 characters. Only for the Settings Test
/// button, which sends a fixed sample sentence: never log it, and never show it for a real subtitle request.
/// </param>
/// <param name="unusableAnswer">
/// The AI answered, but the text is not a translation that can be used (empty, cut off, far too long, or with the
/// model's own notes in it). That says something about this one line, not that the AI is down.
/// </param>
public sealed class AiTranslationException(AiErrorKind kind, int? statusCode, string? providerMessage = null,
    bool unusableAnswer = false)
    : Exception($"AI {kind} ({statusCode})")
{
    public AiErrorKind Kind { get; } = kind;
    public int? StatusCode { get; } = statusCode;
    public string? ProviderMessage { get; } = providerMessage;
    public bool UnusableAnswer { get; } = unusableAnswer;
}
