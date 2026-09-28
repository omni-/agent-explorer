namespace AgentExplorer.Core.Models;

/// <summary>A file or one JSONL member of a DSH ZIP. A null format requests content detection.</summary>
public sealed record TranscriptInput(string Path, TranscriptFormat? Format = null, string? ArchiveEntry = null);
