namespace AgentExplorer.Core.Models;

public sealed record ToolActivity
{
    public string? Name { get; init; }

    public string? CallId { get; init; }

    /// <summary>Native call ID, or an explicit structural key for a Markdown tool block.</summary>
    public string? CorrelationKey { get; init; }

    public string? Arguments { get; init; }

    public OperationOutcome Outcome { get; init; }
}
