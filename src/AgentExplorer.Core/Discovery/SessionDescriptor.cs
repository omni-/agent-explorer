using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Discovery;

/// <summary>Alternatives share native session identity; they are not merged. JSON exports outrank Markdown.</summary>
public sealed record SessionDescriptor(AgentSource Source, SessionMetadata Metadata, TranscriptInput PreferredInput,
    IReadOnlyList<TranscriptInput> AlternativeInputs, bool MetadataIsPartial = true);
