using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Parsing;

internal sealed class ParsingContext(TranscriptFormat format)
{
    internal TranscriptFormat Format { get; } = format;
    internal AgentSource Source { get; } = format switch
    {
        TranscriptFormat.CodexJsonl => AgentSource.Codex,
        TranscriptFormat.ClaudeCodeJsonl => AgentSource.ClaudeCode,
        TranscriptFormat.DshJsonl => AgentSource.Dsh,
        _ => AgentSource.OpenCode
    };
    internal string? Turn { get; set; }
    internal string? Model { get; set; }
    internal string? Provider { get; set; }
    internal bool UnsupportedVersion { get; set; }

    internal ParsingContext Copy() => new(Format)
    {
        Turn = Turn,
        Model = Model,
        Provider = Provider,
        UnsupportedVersion = UnsupportedVersion
    };

    internal TranscriptEvent Event(NativeRecord native, EventKind kind, string type, DateTimeOffset? timestamp = null,
        string? pointer = null) => new()
        {
            Id = "",
            Source = Source,
            Format = Format,
            Layer = Source == AgentSource.OpenCode ? ActivityLayer.Export : ActivityLayer.Source,
            Kind = kind,
            NativeType = type,
            Native = native,
            NativePointer = pointer,
            Timestamp = timestamp,
            TurnId = Turn,
            Model = Model,
            Provider = Provider
        };
}
