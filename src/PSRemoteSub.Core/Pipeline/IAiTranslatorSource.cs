using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Translation.Ai;

namespace PSRemoteSub.Core.Pipeline;

/// <summary>Which kind of translator a <see cref="AiTranslatorHandle"/> holds.</summary>
public enum TranslatorKind
{
    /// <summary>An AI model: gets context lines and game notes, transliterates names, teaches the library matcher.</summary>
    Ai,

    /// <summary>Google Translate: text only (no context, notes or names), and its answers are not taught to the matcher.</summary>
    Google,
}

/// <summary>The translator for the current translation settings: an AI model or Google Translate.</summary>
/// <param name="Translator">Null when the settings cannot produce one (see <paramref name="ConfigErrorKey"/>).</param>
/// <param name="ConfigErrorKey">
/// Resource key (for example <c>AiKeyNeeded</c>) when the settings are not usable. Lines are then published without
/// a translation; there is no other translator to fall back on.
/// </param>
/// <param name="Generation">Changes whenever the translation settings change; clears every pause.</param>
/// <param name="ModelLabel"><c>Provider/model</c> (or <c>google</c>), to tag lines it translated; null when there is no translator.</param>
/// <param name="BriefBudget">Characters of game notes to send with each line (small for local models; unused for Google).</param>
/// <param name="Kind">What the translator is: decides what the lane sends it and whether its answers are learned.</param>
public sealed record AiTranslatorHandle(IAiTranslator? Translator, string? ConfigErrorKey, long Generation,
    string? ModelLabel = null, int BriefBudget = GameBriefBuilder.CloudBudget, TranslatorKind Kind = TranslatorKind.Ai);

/// <summary>
/// Supplies the translator (AI or Google) for the current settings. Read on every subtitle, from any thread, and under the AI
/// lane's lock: <see cref="Current"/> must be a cheap read that takes no lock and never calls back into the lane.
/// </summary>
public interface IAiTranslatorSource
{
    /// <summary>Always a handle: a usable translator, or the config problem that keeps one from being built.</summary>
    AiTranslatorHandle Current { get; }
}

/// <summary>What the AI lane needs from the config the line was read under.</summary>
/// <param name="TargetCode">Target language code, for example <c>th</c>.</param>
/// <param name="ContextLines">Previous subtitle pairs to send as context.</param>
/// <param name="SpeakerLabels">
/// The speaker name as read (not the shown name) for each line of the entry, null for a line without one; null when
/// the line has no names. For the game notes and the learned lines' speakers.
/// </param>
/// <param name="Library">
/// The library the line was read under (null when none applied): the game notes sent with it and the library its
/// answer is learned into. Captured per line, so a line still waiting when the library changes keeps its own.
/// </param>
/// <param name="Learn">Whether the zone's AI answers are remembered by the matcher for this session.</param>
/// <param name="KnownParts">
/// Bubbles zones: the Thai already known for each part (line) of the entry's dialogue, null for a part the AI must
/// translate. Only the unknown parts are sent, and the answer is merged back in order. Null sends the whole line.
/// </param>
/// <param name="Memory">
/// Bubbles zones: the zone's bubbles already translated. The parts the answer translates (one line each) are put in it.
/// </param>
public sealed record AiJobContext(SourceLanguage Source, string TargetCode, string? GameTitle, int ContextLines,
    IReadOnlyList<string?>? SpeakerLabels = null, ActiveLibrary? Library = null, bool Learn = true,
    IReadOnlyList<string?>? KnownParts = null, PartMemory? Memory = null);
