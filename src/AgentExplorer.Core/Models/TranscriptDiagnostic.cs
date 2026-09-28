namespace AgentExplorer.Core.Models;

public sealed record TranscriptDiagnostic(string Code, string Message, string Severity = "warning");
