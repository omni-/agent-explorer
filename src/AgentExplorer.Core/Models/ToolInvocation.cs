namespace AgentExplorer.Core.Models;

/// <summary>All records sharing an explicit correlation key. Multiple calls are ambiguous, not guessed.</summary>
public sealed record ToolInvocation(string CorrelationKey, IReadOnlyList<TranscriptEvent> Calls, IReadOnlyList<TranscriptEvent> Results)
{
    public bool IsAmbiguous => Calls.Count > 1;

    public bool IsMissingCall => Calls.Count == 0;

    public bool IsMissingResult => Results.Count == 0;
}
