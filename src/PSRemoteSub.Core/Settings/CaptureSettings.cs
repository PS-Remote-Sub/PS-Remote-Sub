using System.Text.Json.Serialization;

namespace PSRemoteSub.Core.Settings;

/// <summary>Where the game picture comes from.</summary>
public enum PictureSource
{
    RemotePlay,
    CaptureCard,
}

/// <summary>A capture mode the user picked: frame size and frame rate.</summary>
public sealed record CaptureModeSetting(int Width, int Height, double Fps);

/// <summary>Position and size of the capture window on screen, in pixels.</summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height);

/// <summary>Settings of the capture-card picture source.</summary>
public sealed class CaptureSettings
{
    /// <summary>The chosen capture device; null until one is picked.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceId { get; set; }

    /// <summary>The chosen mode; null means automatic.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CaptureModeSetting? Mode { get; set; }

    /// <summary>Audio volume, 0 to 1.</summary>
    public double Volume { get; set; } = 1.0;

    public bool Muted { get; set; }

    /// <summary>Where the capture window was last; null until it has been placed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WindowPlacement? Window { get; set; }

    public bool FullScreen { get; set; }
}
