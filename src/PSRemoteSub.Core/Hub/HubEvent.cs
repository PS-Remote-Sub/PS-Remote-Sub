namespace PSRemoteSub.Core.Hub;

public abstract record HubEvent;

public sealed record SubtitlePublished(SubtitleEntry Entry) : HubEvent;

/// <summary>No subtitle on screen any more: the overlay hides. History is kept. A null zone means all zones.</summary>
public sealed record SubtitleCleared(string? ZoneId = null) : HubEvent;

/// <summary>Hide a zone's subtitle now and drop its waiting lines and late answers (trigger off, zone removed or its reading changed, bubbles with a trigger moving).</summary>
public sealed record ZoneHidden(string ZoneId) : HubEvent;

/// <summary>A zone's trigger word was seen (<paramref name="Active"/>) or went away.</summary>
public sealed record TriggerChanged(string ZoneId, bool Active) : HubEvent;

public sealed record StatusChanged(PipelineStatus Status) : HubEvent;

public sealed record ClientsChanged(int Count) : HubEvent;

/// <summary>An existing entry was replaced in place, by the AI translation or by clearing its pending flag.</summary>
public sealed record SubtitleRefined(SubtitleEntry Entry) : HubEvent;
