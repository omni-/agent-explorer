using AgentExplorer.Core.Analysis;
using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Indexing;

/// <summary>One indexed file or archive member. Same-session exports remain independent inputs.</summary>
public sealed record IndexedInput
{
    public required string Id { get; init; }

    public required TranscriptInput Input { get; init; }

    public required AgentSource Source { get; init; }

    public required SessionMetadata Metadata { get; init; }

    /// <summary>Identifies the source bytes, format, normalization version, and decoding limits.</summary>
    public required string Revision { get; init; }

    /// <summary>SHA-256 of the file bytes; archive members use the containing archive's hash.</summary>
    public required string ContentSha256 { get; init; }

    public long ByteLength { get; init; }

    public int NormalizationVersion { get; init; }

    public required SessionStatistics Statistics { get; init; }
}
