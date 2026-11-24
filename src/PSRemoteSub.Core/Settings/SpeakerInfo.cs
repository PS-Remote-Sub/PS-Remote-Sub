namespace PSRemoteSub.Core.Settings;

/// <summary>What the app has learned about one speaker name: its Thai spelling and the game's colour for it.</summary>
public sealed class SpeakerInfo
{
    /// <summary>The name in Thai script, or null until it has been translated.</summary>
    public string? Thai { get; set; }

    /// <summary><c>#RRGGBB</c> sampled from the game, or null until a sample succeeded.</summary>
    public string? Color { get; set; }

    public SpeakerInfo Copy() => new() { Thai = Thai, Color = Color };
}
