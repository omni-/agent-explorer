using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Analysis;

/// <summary>One accounting series. Series overlap and must never be added together.</summary>
public sealed record UsageStatistics
{
    public required string Series { get; init; }

    public UsageBasis Basis { get; init; }

    public int Samples { get; init; }

    public required TokenUsage Tokens { get; init; }

    public required IReadOnlyDictionary<string, int> KnownCounterSamples { get; init; }

    public decimal? Cost { get; init; }

    public string? Currency { get; init; }

    public long? MaximumContextWindow { get; init; }

    public long? MaximumContextTokens { get; init; }

    public int CounterRegressions { get; init; }
}
