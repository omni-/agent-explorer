namespace AgentExplorer.Core.Models;

/// <summary>A requested file operation is not evidence that the file was changed.</summary>
public sealed record FileActivity(string Path, string Operation, bool IsReported = false);
