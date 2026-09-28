namespace AgentExplorer.Core.Models;

/// <summary>Record positions refer to decoded content. ZIP members are never extracted.</summary>
public sealed record SourceLocation(string Path, string? ArchiveEntry, long RecordIndex, long? LineNumber = null,
    string? JsonPointer = null);
