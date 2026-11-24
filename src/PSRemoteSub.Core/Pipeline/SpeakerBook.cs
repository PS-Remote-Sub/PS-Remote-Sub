using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Pipeline;

/// <summary>
/// The speaker names known for one game profile: Thai spelling and colour, keyed by the source name (case-insensitive).
/// A name whose translation failed is not tried again for <see cref="RetryAfter"/>. Thread-safe.
/// </summary>
public sealed class SpeakerBook(IReadOnlyDictionary<string, SpeakerInfo>? known, TimeProvider time)
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    /// <summary>Stable subtitles a new name must appear in before it is translated, coloured and saved.</summary>
    public const int SightingsToTrust = 2;

    private readonly Dictionary<string, int> _sightings = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, SpeakerInfo> _names = Seed(known);
    private readonly Dictionary<string, DateTimeOffset> _attempts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A copy of what is known about <paramref name="name"/>, or null.</summary>
    public SpeakerInfo? Get(string name)
    {
        lock (_gate)
            return _names.TryGetValue(name, out var info) ? info.Copy() : null;
    }

    /// <summary>Counts one stable subtitle for each distinct name in <paramref name="names"/>.</summary>
    public void Sight(IEnumerable<string> names)
    {
        lock (_gate)
            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
                _sightings[name] = _sightings.GetValueOrDefault(name) + 1;
    }

    /// <summary>
    /// A name the profile already knows, or one seen in <see cref="SightingsToTrust"/> stable subtitles. A one-off
    /// "Word:" (a sentence that happens to look like a label, an OCR slip) is never translated, coloured or saved.
    /// </summary>
    public bool IsTrusted(string name)
    {
        lock (_gate)
            return TrustedLocked(name);
    }

    /// <summary>Distinct trusted names (case-insensitive, first spelling kept) with no Thai yet and no attempt in the last minute.</summary>
    public IReadOnlyList<string> NamesToTranslate(IEnumerable<string> names)
    {
        var now = time.GetUtcNow();
        lock (_gate)
            return [.. names.Distinct(StringComparer.OrdinalIgnoreCase).Where(n =>
                TrustedLocked(n)
                && !(_names.TryGetValue(n, out var info) && info.Thai is not null)
                && !(_attempts.TryGetValue(n, out var at) && now - at < RetryAfter))];
    }

    private bool TrustedLocked(string name) =>
        _names.ContainsKey(name) || _sightings.GetValueOrDefault(name) >= SightingsToTrust;

    public void MarkAttempt(string name)
    {
        lock (_gate)
            _attempts[name] = time.GetUtcNow();
    }

    /// <summary>True when the Thai spelling changed.</summary>
    public bool SetThai(string name, string thai)
    {
        lock (_gate)
        {
            var info = Entry(name);
            if (info.Thai == thai)
                return false;
            info.Thai = thai;
            return true;
        }
    }

    /// <summary>
    /// Keeps the first real colour seen for a name; a white or near-white one (see <see cref="SpeakerPalette.IsMissing"/>)
    /// may still be replaced by a real colour read later. True when <paramref name="color"/> was stored.
    /// </summary>
    public bool SetColorIfMissing(string name, string color)
    {
        lock (_gate)
        {
            var info = Entry(name);
            if (!SpeakerPalette.IsMissing(info.Color) || string.Equals(info.Color, color, StringComparison.OrdinalIgnoreCase))
                return false;
            info.Color = color;
            return true;
        }
    }

    private SpeakerInfo Entry(string name)
    {
        if (!_names.TryGetValue(name, out var info))
            _names[name] = info = new SpeakerInfo();
        return info;
    }

    private static Dictionary<string, SpeakerInfo> Seed(IReadOnlyDictionary<string, SpeakerInfo>? known)
    {
        var d = new Dictionary<string, SpeakerInfo>(StringComparer.OrdinalIgnoreCase);
        if (known is not null)
            foreach (var (k, v) in known)
                d[k] = v.Copy();
        return d;
    }
}
