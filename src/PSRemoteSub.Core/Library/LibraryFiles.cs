namespace PSRemoteSub.Core.Library;

/// <summary>The files of one game's library folder (<c>library/&lt;game&gt;/</c>) and of its release zip.</summary>
public static class LibraryFiles
{
    /// <summary>The library version, the subtitle count and the sha256 of <see cref="Subtitles"/>. Written by pack.</summary>
    public const string Manifest = "manifest.json";

    /// <summary>One translated subtitle per line: <c>{"en","th","source","scene"?,"speaker"?}</c>.</summary>
    public const string Subtitles = "subtitles.jsonl";

    /// <summary>The game's title, setting, characters and places, sent to the AI as context.</summary>
    public const string World = "world.json";

    /// <summary>Terms (slang, jargon, brand names) and how to render them in Thai.</summary>
    public const string Terms = "terms.json";

    /// <summary>The Thai translation guide; its head is sent to the AI with every line.</summary>
    public const string Guide = "translation-guide.md";

    /// <summary>Files a library may leave out.</summary>
    public static readonly IReadOnlyList<string> Optional = [Terms, World, Guide];

    /// <summary>Every library file, required ones first.</summary>
    public static readonly IReadOnlyList<string> All = [Manifest, Subtitles, .. Optional];
}
