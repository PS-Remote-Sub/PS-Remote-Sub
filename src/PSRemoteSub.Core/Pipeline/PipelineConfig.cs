using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Pipeline;

/// <param name="Zones">The profile's OCR zones, read in this order each tick.</param>
/// <param name="TargetLang">Target language code, for example <c>th</c>.</param>
/// <param name="GameTitle">Optional game name that gives the AI context.</param>
/// <param name="ContextLines">Previous subtitle pairs sent to the AI as context.</param>
/// <param name="ProfileName">Active game profile; switching profiles resets the AI context.</param>
/// <param name="Speakers">Speaker names the profile already knows (Thai spelling and colour), read when the profile changes.</param>
/// <param name="SpeakerColors">False: speaker tags carry no colour (names are drawn in the default colour).</param>
public sealed record PipelineConfig(
    IReadOnlyList<ZoneConfig> Zones,
    SourceLanguage Source,
    string TargetLang,
    bool Paused,
    string? GameTitle = null,
    int ContextLines = 4,
    string? ProfileName = null,
    IReadOnlyDictionary<string, SpeakerInfo>? Speakers = null,
    bool SpeakerColors = true);
