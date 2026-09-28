namespace AgentExplorer.Core.Indexing;

/// <summary>A bounded page. Continue with the same filters; a changed index invalidates the token.</summary>
public sealed record IndexPage<T>(IReadOnlyList<T> Items, string IndexRevision, string? ContinuationToken);
