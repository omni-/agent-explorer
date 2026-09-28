using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Analysis;

public sealed record SessionStatistics
{
    public long NativeRecords { get; init; }

    public long Events { get; init; }

    public long DuplicateEvents { get; init; }

    public long MirrorEvents { get; init; }

    public long DiagnosticEvents { get; init; }

    public DateTimeOffset? FirstTimestamp { get; init; }

    public DateTimeOffset? LastTimestamp { get; init; }

    /// <summary>Observed timestamp range, not active work time or proof of session completion.</summary>
    public TimeSpan? ObservedDuration { get; init; }

    /// <summary>Explicit turn starts only. Null when the source exposes no turn-start records.</summary>
    public long? TurnCount { get; init; }

    public long TurnEndCount { get; init; }

    public long UserMessageCount { get; init; }

    public long AssistantMessageCount { get; init; }

    public long Errors { get; init; }

    public long Compactions { get; init; }

    public long CallsWithoutResults { get; init; }

    public long ResultsWithoutCalls { get; init; }

    public long AmbiguousToolKeys { get; init; }

    public required IReadOnlyDictionary<EventKind, long> EventCounts { get; init; }

    /// <summary>Counts of non-mirror tool-call observations. Use ToolUsageByLayer to separate requests from executions.</summary>
    public required IReadOnlyDictionary<string, long> ToolUsage { get; init; }

    public required IReadOnlyDictionary<ActivityLayer, IReadOnlyDictionary<string, long>> ToolUsageByLayer { get; init; }

    /// <summary>Separate evidence layers, which can describe overlapping executions.</summary>
    public required IReadOnlyDictionary<string, CommandStatistics> Commands { get; init; }

    public required IReadOnlyList<string> RequestedFiles { get; init; }

    public required IReadOnlyList<string> ReportedFiles { get; init; }

    public required IReadOnlyList<RepeatedToolActivity> RepeatedTools { get; init; }

    public required IReadOnlyList<UsageStatistics> Usage { get; init; }

    public required IReadOnlyList<string> Models { get; init; }

    public required IReadOnlyList<string> Providers { get; init; }
}
