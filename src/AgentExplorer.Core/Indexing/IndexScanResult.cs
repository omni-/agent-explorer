using AgentExplorer.Core.Discovery;

namespace AgentExplorer.Core.Indexing;

/// <summary>Failed inputs keep their previous revisions. Issues identify failures and parser diagnostics.</summary>
public sealed record IndexScanResult(string IndexRevision, int UpdatedInputs, int UnchangedInputs,
    int RemovedInputs, IReadOnlyList<DiscoveryIssue> Issues);
