namespace AgentExplorer.Core.Models;

public enum EventKind
{
    Metadata,
    Message,
    Reasoning,
    ToolCall,
    ToolResult,
    Command,
    FileActivity,
    Error,
    TurnStart,
    TurnEnd,
    StepStart,
    StepEnd,
    Context,
    Compaction,
    Usage,
    Attachment,
    Unknown,
    Diagnostic
}
