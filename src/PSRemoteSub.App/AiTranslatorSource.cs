using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Pipeline;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;
using PSRemoteSub.Core.Translation;
using PSRemoteSub.Core.Translation.Ai;

namespace PSRemoteSub.App;

/// <summary>
/// The translator (Google or AI, per <see cref="TranslationSettings.Mode"/>) for the live translation settings. <see cref="Current"/> is read by the AI lane from any thread;
/// <see cref="Rebuild"/> swaps in a new handle with a higher <see cref="AiTranslatorHandle.Generation"/>.
/// </summary>
/// <remarks>
/// <see cref="Rebuild"/> makes no network calls (it only builds clients and decrypts the stored key), so the UI thread
/// may call it. Thread-safe: rebuilds are serialized and the handle is published with a volatile write.
/// </remarks>
public sealed class AiTranslatorSource : IAiTranslatorSource
{
    private readonly Func<TranslationSettings> _settings;
    private readonly ISecretProtector _protector;
    private readonly HttpClient _aiHttp;
    private readonly HttpClient _googleHttp;
    private readonly Lock _gate = new();
    private AiTranslatorHandle _current;
    private long _generation;

    /// <summary>Builds the first handle from the current settings at once, so <see cref="Current"/> is never null.</summary>
    public AiTranslatorSource(Func<TranslationSettings> settings, ISecretProtector protector, HttpClient aiHttp, HttpClient googleHttp)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(aiHttp);
        ArgumentNullException.ThrowIfNull(googleHttp);
        _settings = settings;
        _protector = protector;
        _aiHttp = aiHttp;
        _googleHttp = googleHttp;
        _current = Build();
    }

    /// <summary>Always a handle: a usable translator, or the config problem (for example <c>AiKeyNeeded</c>).</summary>
    public AiTranslatorHandle Current => Volatile.Read(ref _current);

    /// <summary>Re-resolves the translator from the current settings and moves to the next generation. Never throws.</summary>
    public void Rebuild()
    {
        lock (_gate)
            Volatile.Write(ref _current, Build());
    }

    /// <summary>
    /// Resolves a translator for unsaved settings (Settings window Test button) without touching <see cref="Current"/>.
    /// <paramref name="typedKey"/>, when not blank, is used instead of the stored key.
    /// </summary>
    public AiTranslatorResolution ResolveForTest(TranslationSettings draft, string? typedKey)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return AiTranslatorFactory.Resolve(draft, _protector, _aiHttp, typedKey);
    }

    /// <summary>Under <see cref="_gate"/> (or from the constructor). Never throws.</summary>
    private AiTranslatorHandle Build()
    {
        AiTranslatorResolution resolution;
        try
        {
            if (_settings().Mode == TranslationMode.Google)
            {
                _generation++;
                return new AiTranslatorHandle(
                    new GoogleLaneTranslator(new CachingTranslator(new GoogleTranslator(_googleHttp, TimeProvider.System))),
                    null, _generation, ModelLabel: "google", Kind: TranslatorKind.Google);
            }
            resolution = AiTranslatorFactory.Resolve(_settings(), _protector, _aiHttp);
        }
        catch (Exception ex)
        {
            // Type only: the settings hold the (protected) key.
            Log.Warn($"AI translator setup failed: {ex.GetType().Name}");
            resolution = new AiTranslatorResolution(null, AiRefiner.AiDownKey);
        }
        _generation++;
        return new AiTranslatorHandle(resolution.Translator, resolution.ConfigErrorKey, _generation,
            resolution.ModelLabel, resolution.BriefBudget);
    }
}
