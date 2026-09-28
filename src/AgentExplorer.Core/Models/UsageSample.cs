namespace AgentExplorer.Core.Models;

/// <summary>Series and key prevent summing repeated snapshots or unrelated accounting layers.</summary>
public sealed record UsageSample
{
    public required string Series { get; init; }

    public required string Key { get; init; }

    public UsageBasis Basis { get; init; }

    public TokenUsage Tokens { get; init; } = new();

    public long? ContextWindow { get; init; }

    public long? ContextTokens { get; init; }

    public decimal? Cost { get; init; }

    public string? Currency { get; init; }
}
