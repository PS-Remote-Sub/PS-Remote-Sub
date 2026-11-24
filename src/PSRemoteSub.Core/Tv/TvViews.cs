using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Layout;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Tv;

/// <summary>Builds the TV view of a zone from what the PC overlay shows.</summary>
public static class TvViews
{
    /// <summary>
    /// The zone's current line as the TV draws it, or null when there is no line, no caption box, or the client size is unknown.
    /// Every rectangle is mapped from the client area to the game frame.
    /// </summary>
    public static TvZoneView? From(OcrZone zone, OverlayLine? current, double fontSize, int clientWidth, int clientHeight)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (current is null || clientWidth <= 0 || clientHeight <= 0 || zone.GetCaptionRegion() is not { } caption)
            return null;

        RegionFraction? Map(RegionFraction? r) => r is null ? null : FrameMapping.ToFrame(r, clientWidth, clientHeight);

        var rows = CaptionRows.Split(current.Text, current.Speakers, current.RowBounds)
            .Select(r => new TvRow(r.Speaker, r.Text, Map(r.Cover)))
            .ToList();
        return new TvZoneView(zone.Placement == OverlayPlacement.Fit ? TvPlacement.Fit : TvPlacement.Custom,
            Map(caption)!, Map(zone.Region), Map(current.Bounds), fontSize, current.AwaitingAi, rows);
    }
}
