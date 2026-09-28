using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Indexing;

/// <summary>Exact filters on input identity and complete indexed metadata.</summary>
public sealed record IndexInputQuery
{
    public string? InputId { get; init; }

    public AgentSource? Source { get; init; }

    public string? SessionId { get; init; }

    public string? Workspace { get; init; }
}
