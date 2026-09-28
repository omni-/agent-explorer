using System.Text.Json;

using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Parsing;

internal static class DshParser
{
    internal static IEnumerable<TranscriptEvent> Parse(NativeRecord native, JsonElement root, ParsingContext context)
    {
        var type = root.Str("type") ?? "unknown";
        var data = root.Get("data");
        var time = root.Get("time").Time() ?? root.Get("createdAt").Time();
        var item = context.Event(native, EventKind.Unknown, type, time, "/data") with
        {
            NativeId = root.Str("seq"),
            TurnId = data.Str("turn") ?? context.Turn,
            StepId = data.Str("step"),
            Layer = ActivityLayer.Execution
        };
        if (type == "session")
        {
            context.UnsupportedVersion = root.Num("version") != 3;
            yield return item with
            {
                Kind = EventKind.Metadata,
                NativePointer = "",
                Metadata = new SessionMetadata { SessionId = root.Str("id"), ParentSessionId = root.Str("parentSession"), Workspace = root.Str("cwd"), FormatVersion = root.Str("version"), CreatedAt = time }
            };
            if (context.UnsupportedVersion)
            {
                yield return item with { Kind = EventKind.Diagnostic, Diagnostic = new TranscriptDiagnostic("unsupported-version", "Only DSH storage version 3 has been verified. Other generations remain native unknown records.") };
            }

            yield break;
        }
        if (context.UnsupportedVersion) { yield return item; yield break; }
        switch (type)
        {
            case "turn/start":
                context.Turn = data.Str("turn");
                yield return item with { Kind = EventKind.TurnStart, TurnId = context.Turn };
                break;
            case "turn/end":
                yield return item with { Kind = EventKind.TurnEnd };
                break;
            case "step/start": yield return item with { Kind = EventKind.StepStart }; break;
            case "step/end": yield return item with { Kind = EventKind.StepEnd }; break;
            case "request/header":
                var config = data.Get("header").Get("config");
                context.Model = config.Str("model") ?? context.Model;
                context.Provider = config.Str("provider") ?? context.Provider;
                yield return item with
                {
                    Kind = EventKind.Context,
                    Model = context.Model,
                    Provider = context.Provider,
                    Metadata = new SessionMetadata { Model = config.Str("model"), Provider = config.Str("provider") }
                };
                break;
            case "request/context":
                context.Model = data.Str("model") ?? context.Model;
                context.Provider = data.Str("provider") ?? context.Provider;
                yield return item with
                {
                    Kind = EventKind.Context,
                    Model = context.Model,
                    Provider = context.Provider,
                    Metadata = new SessionMetadata { Model = data.Str("model"), Provider = data.Str("provider") },
                    Usage = new UsageSample { Series = "dsh.context", Key = "context", Basis = UsageBasis.Context, ContextWindow = data.Num("contextWindow"), ContextTokens = data.Num("contextTokens") }
                };
                break;
            case "assistant/message":
            case "user/message":
            case "system/message":
            case "tool/result":
                var message = data.Get("message");
                if (message.ValueKind == JsonValueKind.Undefined)
                {
                    message = data;
                }

                var source = message.Get("source");
                var parent = item with
                {
                    Role = message.Str("role"),
                    Model = source.Str("model") ?? context.Model,
                    Provider = source.Str("provider") ?? context.Provider,
                    NativeId = message.Str("id") ?? item.NativeId,
                    MessageId = message.Str("id")
                };
                var prefix = data.Get("message").ValueKind == JsonValueKind.Undefined ? "/data/content" : "/data/message/content";
                var index = 0;
                var content = message.Get("content");
                foreach (var block in content.Items())
                {
                    var blockType = block.Str("type");
                    var part = parent with { NativeType = type + ":" + blockType, NativePointer = prefix + "/" + index++ };
                    switch (blockType)
                    {
                        case "text": yield return part with { Kind = EventKind.Message, Text = block.Str("text") }; break;
                        case "reasoning": yield return part with { Kind = EventKind.Reasoning, Text = block.Str("text") }; break;
                        case "tool-call":
                            // The separate durable tool/call proves execution was started; this block records the request.
                            yield return ToolParsing.Call(part, block.Str("name"), block.Str("id"), JsonValue.ParseArguments(block.Str("arguments")), block.Str("arguments")) with { IsMirror = true, Layer = ActivityLayer.Model };
                            break;
                        case "tool-result":
                            var text = block.Get("content").ContentText();
                            yield return part with
                            {
                                Kind = EventKind.ToolResult,
                                Text = text,
                                Tool = new ToolActivity { CallId = block.Str("toolCallId") ?? source.Str("callId"), CorrelationKey = block.Str("toolCallId") ?? source.Str("callId"), Outcome = ToolParsing.Outcome(block) },
                                Command = ToolParsing.ResultCommand(block, text, AgentSource.Dsh)
                            };
                            break;
                        case "image":
                        case "file": yield return part with { Kind = EventKind.Attachment }; break;
                        default: yield return part; break;
                    }
                }
                if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() == 0)
                {
                    yield return parent with { Kind = EventKind.Message, Text = content.ContentText() };
                }

                if (data.Get("usage").ValueKind == JsonValueKind.Object)
                {
                    yield return parent with
                    {
                        Kind = EventKind.Usage,
                        NativePointer = "/data/usage",
                        Usage = new UsageSample { Series = "dsh.response", Key = message.Str("id") ?? "seq:" + root.Str("seq"), Basis = UsageBasis.Response, Tokens = UsageParsing.Tokens(data.Get("usage")) }
                    };
                }

                break;
            case "tool/call":
                var arguments = data.Get("arguments");
                yield return ToolParsing.Call(item, data.Str("name"), data.Str("callId"), arguments.ValueKind == JsonValueKind.String ? JsonValue.ParseArguments(arguments.String()) : arguments,
                    arguments.ValueKind == JsonValueKind.String ? arguments.String() : arguments.Raw());
                break;
            case "session/title": yield return item with { Kind = EventKind.Metadata, Metadata = new SessionMetadata { Title = data.Str("title") } }; break;
            case "deliverables/presented":
                yield return item with { Kind = EventKind.FileActivity, Files = data.Get("files").Items().Where(x => x.Str("path") is not null).Select(x => new FileActivity(x.Str("path")!, "present", true)).ToArray() };
                break;
            case "assistant/attempt":
                yield return item with { Kind = data.Get("error").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) ? EventKind.Error : EventKind.Context, Text = data.Get("error").ContentText() };
                break;
            case "compaction":
            case "context/compacted":
            case "session/compacted": yield return item with { Kind = EventKind.Compaction }; break;
            default:
                // Slash commands are harness commands, not shell executions.
                yield return item with { Kind = type is "command/run" or "command/done" or "agent/inbox/spliced" ? EventKind.Context : EventKind.Unknown };
                break;
        }
    }
}
