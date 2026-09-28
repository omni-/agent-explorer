using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Analysis;

internal static class MetadataMerge
{
    internal static SessionMetadata Apply(SessionMetadata current, SessionMetadata update) => new()
    {
        SessionId = update.SessionId ?? current.SessionId,
        ParentSessionId = update.ParentSessionId ?? current.ParentSessionId,
        Title = update.Title ?? current.Title,
        Workspace = update.Workspace ?? current.Workspace,
        ProjectId = update.ProjectId ?? current.ProjectId,
        Model = update.Model ?? current.Model,
        Provider = update.Provider ?? current.Provider,
        AgentVersion = update.AgentVersion ?? current.AgentVersion,
        FormatVersion = update.FormatVersion ?? current.FormatVersion,
        CreatedAt = update.CreatedAt ?? current.CreatedAt,
        UpdatedAt = update.UpdatedAt ?? current.UpdatedAt
    };
}
