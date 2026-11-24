using PSRemoteSub.Core.Translation;

namespace PSRemoteSub.Core.Pipeline;

/// <summary>
/// One Bubbles zone's bubbles already translated this session: a bubble's text (without the sender's name) → its Thai,
/// the <see cref="Capacity"/> most recently used. Thread-safe: the tick reads it, the AI lane writes it.
/// </summary>
public sealed class PartMemory
{
    public const int Capacity = 300;

    private readonly Lock _gate = new();
    private readonly LruCache<string, string> _parts = new(Capacity);

    /// <summary>The Thai for <paramref name="source"/>, or null when it is not known.</summary>
    public string? Get(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
            return _parts.TryGet(source, out var thai) ? thai : null;
    }

    public void Put(string source, string thai)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(thai);
        lock (_gate)
            _parts.Set(source, thai);
    }
}
