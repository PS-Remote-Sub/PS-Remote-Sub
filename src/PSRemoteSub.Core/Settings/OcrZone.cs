using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Layout;

namespace PSRemoteSub.Core.Settings;

/// <summary>One OCR zone of a game profile: where to read, where to draw the Thai caption, and what to do with the text.</summary>
public sealed class OcrZone
{
    public const string DialogueName = "บทพูด";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>The OCR box: where text is read, as a fraction of the client area.</summary>
    public RegionFraction? Region { get; set; }

    /// <summary>The caption box: where the Thai subtitle is drawn; null until set.</summary>
    public RegionFraction? CaptionRegion { get; set; }

    public bool TriggerEnabled { get; set; }
    public RegionFraction? TriggerRegion { get; set; }

    /// <summary>Words (comma separated) that make this zone active when seen in the trigger box.</summary>
    public string TriggerWords { get; set; } = "";

    public PreprocessFilter Filter { get; set; } = PreprocessFilter.Normal;
    public OverlayPlacement Placement { get; set; } = OverlayPlacement.Custom;
    public bool SendToWeb { get; set; }
    /// <summary>Whether the zone's AI answers are remembered by the matcher for this session.</summary>
    public bool LearnToLibrary { get; set; }

    /// <summary>Only text with a speaker name (<c>Lucia: …</c>) is shown and translated.</summary>
    public bool SpeakerLinesOnly { get; set; }

    /// <summary>The caption box, or the OCR box until one is set. A method so it is not serialized.</summary>
    public RegionFraction? GetCaptionRegion() => CaptionRegion ?? Region;

    public static OcrZone Dialogue() => new() { Name = DialogueName, SendToWeb = true, LearnToLibrary = true };

    /// <summary>
    /// A new zone as "add zone" makes it (Games page and the box editor): default settings, named after
    /// <paramref name="zones"/> (the zones it joins). Not added to anything.
    /// </summary>
    public static OcrZone CreateNext(IEnumerable<OcrZone> zones) => new() { Name = NextName(zones) };

    /// <summary>"โซน N" with N = count + 1, bumped past names already taken.</summary>
    public static string NextName(IEnumerable<OcrZone> zones)
    {
        var list = zones.ToList();
        var taken = list.Select(z => z.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = list.Count + 1;
        while (taken.Contains($"โซน {n}"))
            n++;
        return $"โซน {n}";
    }
}
