namespace AgentExplorer.Core.Models;

/// <summary>Native counters; cache/reasoning fields can overlap other counters. Do not add fields together.</summary>
public sealed record TokenUsage
{
    public long? Input { get; init; }

    public long? Output { get; init; }

    public long? Total { get; init; }

    public long? CacheRead { get; init; }

    public long? CacheWrite { get; init; }

    public long? Reasoning { get; init; }
}
