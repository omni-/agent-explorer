namespace AgentExplorer.Core.Models;

public sealed record CommandActivity
{
    public string? Command { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? ProcessId { get; init; }

    public int? ExitCode { get; init; }

    public string? StandardOutput { get; init; }

    public string? StandardError { get; init; }

    public OperationOutcome Outcome { get; init; }
}
