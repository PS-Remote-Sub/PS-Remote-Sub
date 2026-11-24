using PSRemoteSub.Core.Diagnostics;

namespace PSRemoteSub.Core.Translation;

/// <summary>
/// Unofficial Google Translate client: primary (gtx) endpoint, then clients5 fallback; after both fail,
/// pauses for 30 s and throws <see cref="TranslationUnavailableException"/> without any HTTP traffic.
/// After 3 consecutive calls where the primary failed but the fallback worked, the fallback is tried first for
/// 10 minutes (then the primary gets another chance), so a blocked primary is not hit on every line.
/// Intended for a single background loop (not thread-safe).
/// </summary>
public sealed class GoogleTranslator : ITranslator
{
    private const string PrimaryBase = "https://translate.googleapis.com/translate_a/single?client=gtx";
    private const string FallbackBase = "https://clients5.google.com/translate_a/t?client=dict-chrome-ex";
    private static readonly TimeSpan BackoffDuration = TimeSpan.FromSeconds(30);
    private const int PrimaryFailuresBeforePreferringFallback = 3;
    private static readonly TimeSpan PreferFallbackDuration = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private DateTimeOffset? _pausedUntil;
    // Consecutive calls where the primary failed but the fallback worked. Not reset when the preference ends, so a
    // primary that is still failing after the 10 minutes switches straight back on its first failure.
    private int _primaryFailures;
    private DateTimeOffset? _preferFallbackUntil;

    /// <remarks>
    /// The caller (App) configures <paramref name="http"/>: <c>Timeout</c> = 5 s and the app's own
    /// User-Agent <c>PSRemoteSub/&lt;version&gt;</c> (not a browser-like one).
    /// </remarks>
    public GoogleTranslator(HttpClient http, TimeProvider time)
    {
        _http = http;
        _time = time;
    }

    public async Task<string> TranslateAsync(string text, string sourceGoogle, string targetGoogle, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (_pausedUntil is { } until)
        {
            if (now < until)
                throw new TranslationUnavailableException("Translator paused after repeated failures.", until);
            _pausedUntil = null;
        }

        var q = Uri.EscapeDataString(text);
        var primaryUrl = $"{PrimaryBase}&sl={sourceGoogle}&tl={targetGoogle}&dt=t&q={q}";
        var fallbackUrl = $"{FallbackBase}&sl={sourceGoogle}&tl={targetGoogle}&q={q}";

        string? failure;
        try
        {
            if (_preferFallbackUntil is { } preferUntil && now < preferUntil)
            {
                // The primary kept failing: don't keep hitting it on every line (anti-goal: don't hammer Google).
                var fallback = await TryAsync(fallbackUrl, GoogleResponseParsers.ParseClients5, ct);
                if (fallback.Value is not null) return Success(fallback.Value);
                failure = "fallback: " + fallback.Error;

                var primary = await TryAsync(primaryUrl, GoogleResponseParsers.ParseGtx, ct);
                if (primary.Value is not null) return PrimarySuccess(primary.Value);
                failure += "; primary: " + primary.Error;
            }
            else
            {
                var primary = await TryAsync(primaryUrl, GoogleResponseParsers.ParseGtx, ct);
                if (primary.Value is not null) return PrimarySuccess(primary.Value);
                failure = "primary: " + primary.Error;

                var fallback = await TryAsync(fallbackUrl, GoogleResponseParsers.ParseClients5, ct);
                if (fallback.Value is not null) return FallbackRescued(fallback.Value);
                failure += "; fallback: " + fallback.Error;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }

        var retryAt = _time.GetUtcNow() + BackoffDuration;
        _pausedUntil = retryAt;
        throw new TranslationUnavailableException(failure, retryAt);
    }

    private string Success(string value)
    {
        _pausedUntil = null;
        return value;
    }

    private string PrimarySuccess(string value)
    {
        _primaryFailures = 0;
        _preferFallbackUntil = null;
        return Success(value);
    }

    /// <summary>The primary failed and the fallback worked: after 3 in a row, prefer the fallback for 10 minutes.</summary>
    private string FallbackRescued(string value)
    {
        if (++_primaryFailures >= PrimaryFailuresBeforePreferringFallback)
        {
            _preferFallbackUntil = _time.GetUtcNow() + PreferFallbackDuration;
            Log.Info($"Translator: primary endpoint failed {_primaryFailures} times in a row; using the fallback first for 10 minutes");
        }
        return Success(value);
    }

    private async Task<(string? Value, string? Error)> TryAsync(string url, Func<string, string> parse, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return (null, $"HTTP {(int)resp.StatusCode}");
            var body = await resp.Content.ReadAsStringAsync(ct);
            return (parse(body), null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "timeout");
        }
        catch (HttpRequestException e)
        {
            return (null, e.Message);
        }
        catch (FormatException e)
        {
            return (null, e.Message);
        }
    }
}
