namespace AgentExplorer.Core.Models;

public sealed record TranscriptEvent
{
    public required string Id { get; init; }

    public required AgentSource Source { get; init; }

    public required TranscriptFormat Format { get; init; }

    public ActivityLayer Layer { get; init; }

    /// <summary>Stable emitted order, independent of optional timestamps.</summary>
    public long Sequence { get; init; }

    public required EventKind Kind { get; init; }

    public required string NativeType { get; init; }

    public required NativeRecord Native { get; init; }

    /// <summary>JSON pointer within Native.Text (not necessarily the entire source file).</summary>
    public string? NativePointer { get; init; }

    public string? NativeId { get; init; }

    public string? MessageId { get; init; }

    public DateTimeOffset? Timestamp { get; init; }

    public string? Role { get; init; }

    public string? Text { get; init; }

    public string? TurnId { get; init; }

    public string? StepId { get; init; }

    public string? Model { get; init; }

    public string? Provider { get; init; }

    public SessionMetadata? Metadata { get; init; }

    public ToolActivity? Tool { get; init; }

    public CommandActivity? Command { get; init; }

    public IReadOnlyList<FileActivity> Files { get; init; } = [];

    public UsageSample? Usage { get; init; }

    public TranscriptDiagnostic? Diagnostic { get; init; }

    /// <summary>A source-provided alternate view, excluded from default factual counts.</summary>
    public bool IsMirror { get; init; }

    /// <summary>An exact replay of a record with the same native identity. The raw record remains available.</summary>
    public string? DuplicateOf { get; init; }

    public bool IsError => Kind == EventKind.Error || Tool?.Outcome == OperationOutcome.Failed ||
        Command?.Outcome == OperationOutcome.Failed;
}
