namespace AgentExplorer.Core.Indexing;

/// <summary>The index changed between pages. Restart the query without a continuation token.</summary>
public sealed class StaleIndexCursorException() : InvalidOperationException("The index changed. Restart the query without a continuation token.");
