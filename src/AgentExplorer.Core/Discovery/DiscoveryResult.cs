namespace AgentExplorer.Core.Discovery;

public sealed record DiscoveryResult(IReadOnlyList<SessionDescriptor> Sessions, IReadOnlyList<DiscoveryIssue> Issues);
