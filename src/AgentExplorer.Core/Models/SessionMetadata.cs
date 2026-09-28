namespace AgentExplorer.Core.Models;

/// <summary>Only values explicitly supplied by the transcript. Metadata events can update these fields.</summary>
public sealed record SessionMetadata
{
    public string? SessionId { get; init; }

    public string? ParentSessionId { get; init; }

    public string? Title { get; init; }

    public string? Workspace { get; init; }

    public string? ProjectId { get; init; }

    public string? Model { get; init; }

    public string? Provider { get; init; }

    public string? AgentVersion { get; init; }

    public string? FormatVersion { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}
