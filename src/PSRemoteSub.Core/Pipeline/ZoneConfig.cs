using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Layout;
using PSRemoteSub.Core.Settings;
using PSRemoteSub.Core.Text;

namespace PSRemoteSub.Core.Pipeline;

/// <param name="Region">The trigger box; null when none is drawn yet (the zone then does not read).</param>
/// <param name="Words">The parsed trigger words; empty means the zone does not read.</param>
public sealed record ZoneTrigger(RegionFraction? Region, IReadOnlyList<string> Words);

/// <summary>What the pipeline needs of one OCR zone, read each tick.</summary>
/// <param name="Region">The OCR box as fractions of the client area; null when none is drawn yet.</param>
/// <param name="SpeakerLinesOnly">Only text with a speaker name is shown and translated (signs and plates in the box are ignored).</param>
/// <param name="Placement"><see cref="OverlayPlacement.Fit"/> (Bubbles) splits a read into bubbles and, with a trigger,
/// hides the zone at once when its text moves; anything else reads as one block, as before zones.</param>
/// <param name="LearnToLibrary">Whether the zone's AI answers are remembered by the matcher for this session.</param>
/// <param name="Trigger">When set, the zone reads only while its trigger words are seen in the trigger box.</param>
public sealed record ZoneConfig(string Id, string Name, RegionFraction? Region, PreprocessFilter Filter,
    bool SpeakerLinesOnly = false, OverlayPlacement Placement = OverlayPlacement.Custom,
    bool SendToWeb = true, bool LearnToLibrary = true, ZoneTrigger? Trigger = null)
{
    public static ZoneConfig From(OcrZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return new ZoneConfig(zone.Id, zone.Name, zone.Region, zone.Filter, zone.SpeakerLinesOnly, zone.Placement,
            zone.SendToWeb, zone.LearnToLibrary,
            zone.TriggerEnabled ? new ZoneTrigger(zone.TriggerRegion, TriggerWords.Parse(zone.TriggerWords)) : null);
    }

    /// <summary>
    /// True when <paramref name="other"/> reads the same text the same way: same boxes, filter, splitting and trigger
    /// words. The name and where the text goes do not count.
    /// </summary>
    public bool SameReading(ZoneConfig other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Region == other.Region && Filter == other.Filter && SpeakerLinesOnly == other.SpeakerLinesOnly
            && Placement == other.Placement && SameTrigger(Trigger, other.Trigger);
    }

    private static bool SameTrigger(ZoneTrigger? a, ZoneTrigger? b) =>
        a is null || b is null
            ? a is null && b is null
            : a.Region == b.Region && a.Words.SequenceEqual(b.Words, StringComparer.Ordinal);
}
