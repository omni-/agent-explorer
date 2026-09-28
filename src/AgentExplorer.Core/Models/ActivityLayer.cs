namespace AgentExplorer.Core.Models;

/// <summary>Model requests and runtime observations can describe overlapping work.</summary>
public enum ActivityLayer
{
    Source,
    Model,
    Execution,
    Export
}
