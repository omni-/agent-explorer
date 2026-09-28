namespace AgentExplorer.Core.Discovery;

public sealed record DiscoveryOptions
{
    public bool UseDefaultLocations { get; init; } = true;

    /// <summary>Codex data directory, containing sessions/ and archived_sessions/. Empty disables it.</summary>
    public string? CodexHome { get; init; }

    /// <summary>Claude data directory, containing projects/. Empty disables it.</summary>
    public string? ClaudeHome { get; init; }

    /// <summary>DSH data directory, containing sessions/. Empty disables it.</summary>
    public string? DshHome { get; init; }

    public IReadOnlyList<string> ExportDirectories { get; init; } = [];

    /// <summary>Discovery reads an initial bounded prefix. Load a session for complete metadata.</summary>
    public int MetadataEventLimit { get; init; } = 32;
}
