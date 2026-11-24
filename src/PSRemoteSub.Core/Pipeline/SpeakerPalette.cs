using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Pipeline;

/// <summary>
/// Colours for speaker names the game's colour could not be read for (or that read as white, which would blend into
/// the white dialogue). Readable on the dark overlay box; never white. A name always gets the same colour.
/// </summary>
public static class SpeakerPalette
{
    public static IReadOnlyList<string> Colors { get; } =
    [
        "#FFD24D", // yellow
        "#7FD7FF", // sky
        "#FF8C8C", // salmon
        "#9BE38B", // green
        "#D9A6FF", // lavender
        "#FFB066", // orange
        "#6FE0C8", // teal
        "#FF9FD0", // pink
    ];

    /// <summary>The colour for <paramref name="name"/>: stable across runs (not <see cref="string.GetHashCode()"/>), case-insensitive.</summary>
    public static string For(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        uint hash = 2166136261; // FNV-1a
        foreach (char c in name.ToUpperInvariant())
        {
            hash ^= c;
            hash *= 16777619;
        }
        return Colors[(int)(hash % (uint)Colors.Count)];
    }

    /// <summary>The palette colour after <paramref name="color"/> (for a second speaker who drew the same one).</summary>
    public static string After(string color)
    {
        int i = Colors.ToList().IndexOf(color);
        return Colors[(i + 1) % Colors.Count];
    }

    /// <summary>
    /// True for a colour that cannot tell a name from the white dialogue: none, white, grey, or white with a faint
    /// tint (as read from glowing or anti-aliased text). Names must never look white.
    /// </summary>
    public static bool IsMissing(string? color) => color is null || NameColorSampler.IsWhitish(color);
}
