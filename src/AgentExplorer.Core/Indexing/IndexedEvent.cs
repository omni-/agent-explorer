using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Indexing;

/// <summary>Event identity is (InputId, Revision, Event.Id), never Event.Id alone.</summary>
public sealed record IndexedEvent(string InputId, string Revision, string? SessionId, TranscriptEvent Event);
