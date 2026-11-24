namespace PSRemoteSub.Core.Hub;

/// <summary>Who says a subtitle line: the name to show (Thai when known) and its colour (<c>#RRGGBB</c>, or null for white).</summary>
public sealed record SpeakerTag(string Name, string? Color);
