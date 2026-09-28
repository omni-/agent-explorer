namespace AgentExplorer.Core.Analysis;

/// <summary>Success/failure counts require a native process exit code, not a tool success flag.</summary>
public sealed record CommandStatistics(long Observations, long ExitCodeSuccesses, long ExitCodeFailures, long UnknownExitCode);
