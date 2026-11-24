using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Tv;

/// <summary>How a zone's caption box is placed: where the user put it, or fitted over the game's own text.</summary>
public enum TvPlacement { Custom, Fit }

/// <summary>Cover draws boxes over the game's text; Strip shows the captions in a band at the bottom.</summary>
public enum TvMode { Cover, Strip }

/// <summary>One caption row; <paramref name="Cover"/> is where the game drew its text (fraction of the game frame), or null.</summary>
public sealed record TvRow(SpeakerTag? Speaker, string Text, RegionFraction? Cover);

/// <summary>
/// What the TV draws for one zone; every rectangle is a fraction of the game frame. <paramref name="Rows"/> compare by
/// content so an unchanged view is recognised as equal.
/// </summary>
public sealed record TvZoneView(TvPlacement Placement, RegionFraction CaptionBox, RegionFraction? OcrBox, RegionFraction? Bounds,
    double FontSize, bool Provisional, IReadOnlyList<TvRow> Rows)
{
    public bool Equals(TvZoneView? other) =>
        other is not null && Placement == other.Placement && CaptionBox == other.CaptionBox && OcrBox == other.OcrBox
        && Bounds == other.Bounds && FontSize.Equals(other.FontSize) && Provisional == other.Provisional
        && Rows.SequenceEqual(other.Rows);

    public override int GetHashCode() => HashCode.Combine(Placement, CaptionBox, OcrBox, Bounds, FontSize, Provisional, Rows.Count);
}

/// <summary>
/// Nudges the TV overlay to line up with the picture: the TV scales its screen about the centre by <paramref name="Scale"/>,
/// then shifts it by the offsets, which are fractions of the TV screen's width / height (not of the scaled frame).
/// </summary>
public sealed record TvCalibration(double Scale = 1.0, double OffsetX = 0, double OffsetY = 0);

public sealed record TvConfig(bool Enabled, string PcName, TvMode Mode, int TextScale, double BoxOpacity, int StripBottom,
    TvCalibration Calibration, bool Calibrating);

/// <summary>A change the feed announces to its listeners (the TV connections).</summary>
public abstract record TvFeedEvent;

/// <summary>A zone's view changed; a null <paramref name="View"/> means the zone shows nothing now.</summary>
public sealed record TvZoneChanged(string ZoneId, TvZoneView? View) : TvFeedEvent;

public sealed record TvConfigChanged(TvConfig Config) : TvFeedEvent;

public sealed record TvClientsChanged(IReadOnlyList<string> Clients) : TvFeedEvent;
