using System.Text.Json.Serialization;
using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Hub;

/// <param name="Id">Increasing from 1 for the life of the hub.</param>
/// <param name="Translated">The AI translation; null while it is awaited and when the line could not be translated.</param>
/// <param name="SourceLang">Source language code (<c>en</c>, <c>ja</c>, ...).</param>
/// <param name="Error">Resource key when translation failed; UI layers localize it. Cleared by a translation.</param>
/// <param name="AwaitingAi">The AI translation of this entry is still pending.</param>
/// <param name="Bounds">Where the game drew the source text, as a fraction of the client area; overlay only.</param>
/// <param name="Dialogue">With speaker names: the source lines without the names (what gets translated), joined by line breaks.</param>
/// <param name="Speakers">With speaker names: one tag per line (null for a line without a name); <see cref="Translated"/> then holds only the dialogue.</param>
/// <param name="RowBounds">Where the game drew each line of the source (one per speaker part), as fractions of the client area; overlay only.</param>
/// <param name="ZoneId">The OCR zone the line came from; null for a profile without zones.</param>
/// <param name="ZoneName">Display name of that zone, shown in the web page.</param>
/// <param name="SendToWeb">False when the zone's caption box is overlay-only; the web page then never sees the entry.</param>
public sealed record SubtitleEntry(
    long Id,
    DateTimeOffset Time,
    string Source,
    string? Translated,
    string SourceLang,
    string? Error,
    bool AwaitingAi = false,
    [property: JsonIgnore] RegionFraction? Bounds = null,
    string? Dialogue = null,
    IReadOnlyList<SpeakerTag?>? Speakers = null,
    [property: JsonIgnore] IReadOnlyList<RegionFraction>? RowBounds = null,
    string? ZoneId = null,
    string? ZoneName = null,
    [property: JsonIgnore] bool SendToWeb = true);
