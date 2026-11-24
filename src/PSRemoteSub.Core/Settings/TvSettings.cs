using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Core.Settings;

/// <summary>Persisted settings of the subtitles shown on the Android TV.</summary>
public sealed class TvSettings
{
    public bool Enabled { get; set; }
    public TvMode Mode { get; set; } = TvMode.Cover;

    /// <summary>Caption text size in percent, 50-200.</summary>
    public int TextScale { get; set; } = 100;

    /// <summary>Opacity of the caption boxes, 0-1.</summary>
    public double BoxOpacity { get; set; } = 0.75;

    /// <summary>Gap between the bottom strip and the screen edge in percent, 0-60.</summary>
    public int StripBottom { get; set; } = 8;

    /// <summary>Calibration scale about the centre, 0.8-1.2.</summary>
    public double CalibScale { get; set; } = 1.0;

    /// <summary>
    /// Calibration offsets, -0.1 to 0.1, as fractions of the TV screen's width / height (not of the scaled game frame):
    /// the TV shifts the scaled frame by <c>offset * screen size</c>.
    /// </summary>
    public double CalibOffsetX { get; set; }
    public double CalibOffsetY { get; set; }

    /// <summary>Identifies this PC to the TV; created once and kept in the settings file.</summary>
    public string PcId { get; set; } = NewPcId();

    /// <summary>Last address the TV was reached at over ADB, if any.</summary>
    public string? AdbAddress { get; set; }

    private static string NewPcId() => Guid.NewGuid().ToString("N");

    /// <summary>Repairs a missing or blank <see cref="PcId"/> a hand-edited file can leave.</summary>
    internal void Normalize()
    {
        if (string.IsNullOrWhiteSpace(PcId)) PcId = NewPcId();
    }

    /// <summary>The config the TV receives, with every value clamped to its range.</summary>
    public TvConfig ToConfig(string pcName, bool calibrating) => new(
        Enabled, pcName, Mode,
        Math.Clamp(TextScale, 50, 200),
        Clamp(BoxOpacity, 0, 1, 0.75),
        Math.Clamp(StripBottom, 0, 60),
        new TvCalibration(Clamp(CalibScale, 0.8, 1.2, 1.0), Clamp(CalibOffsetX, -0.1, 0.1, 0), Clamp(CalibOffsetY, -0.1, 0.1, 0)),
        calibrating);

    private static double Clamp(double v, double min, double max, double fallback) =>
        double.IsNaN(v) ? fallback : Math.Clamp(v, min, max);
}
