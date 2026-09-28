using System.Text.Json;

using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Parsing;

internal static class ClaudeCodeParser
{
    internal static IEnumerable<TranscriptEvent> Parse(NativeRecord native, JsonElement root, ParsingContext context)
    {
        var type = root.Str("type") ?? "unknown";
        var message = root.Get("message");
        var time = root.Get("timestamp").Time();
        var metadata = new SessionMetadata
        {
            SessionId = root.Str("sessionId"),
            Workspace = root.Str("cwd"),
            AgentVersion = root.Str("version"),
            Title = root.Str("customTitle") ?? root.Str("aiTitle"),
            Model = message.Str("model")
        };
        var item = context.Event(native, EventKind.Unknown, type, time) with
        {
            NativeId = root.Str("uuid"),
            Metadata = metadata,
            Model = message.Str("model"),
            MessageId = message.Str("id") ?? root.Str("uuid"),
            Layer = ActivityLayer.Model,
            TurnId = root.Str("promptId"),
            Role = message.Str("role")
        };
        if (type is "user" or "assistant")
        {
            var content = message.Get("content");
            if (content.ValueKind == JsonValueKind.String)
            {
                yield return item with { Kind = EventKind.Message, Text = content.String(), NativePointer = "/message/content" };
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var block in content.EnumerateArray())
                {
                    var blockType = block.Str("type") ?? "unknown";
                    var part = item with { NativeType = type + ":" + blockType, NativePointer = $"/message/content/{index++}" };
                    switch (blockType)
                    {
                        case "text": yield return part with { Kind = EventKind.Message, Text = block.Str("text") }; break;
                        case "thinking":
                        case "redacted_thinking": yield return part with { Kind = EventKind.Reasoning, Text = block.Str("thinking") }; break;
                        case "tool_use":
                        case "server_tool_use":
                            yield return ToolParsing.Call(part, block.Str("name"), block.Str("id"), block.Get("input"));
                            break;
                        case "tool_result":
                            var text = block.Get("content").ContentText();
                            var extra = root.Get("toolUseResult");
                            yield return part with
                            {
                                Kind = EventKind.ToolResult,
                                Text = text,
                                Tool = new ToolActivity { CallId = block.Str("tool_use_id"), CorrelationKey = block.Str("tool_use_id"), Outcome = ToolParsing.Outcome(block) },
                                Command = ToolParsing.ResultCommand(extra, text, AgentSource.ClaudeCode)
                            };
                            break;
                        case "image":
                        case "document": yield return part with { Kind = EventKind.Attachment }; break;
                        default: yield return part; break;
                    }
                }
                if (content.GetArrayLength() == 0)
                {
                    yield return item with { Kind = EventKind.Message };
                }
            }
            else
            {
                yield return item with { Kind = EventKind.Message };
            }

            if (message.Get("usage").ValueKind == JsonValueKind.Object)
            {
                yield return item with
                {
                    Kind = EventKind.Usage,
                    NativePointer = "/message/usage",
                    Usage = new UsageSample
                    {
                        Series = "claude.response",
                        Key = message.Str("id") ?? root.Str("requestId") ?? "record:" + native.Location.RecordIndex,
                        Basis = UsageBasis.Response,
                        Tokens = UsageParsing.Tokens(message.Get("usage"))
                    }
                };
            }

            if (root.Bool("isApiErrorMessage") == true)
            {
                yield return item with { Kind = EventKind.Error, Text = content.ContentText() };
            }

            yield break;
        }
        switch (type)
        {
            case "system":
                var subtype = root.Str("subtype");
                yield return item with
                {
                    Kind = subtype switch
                    {
                        "compact_boundary" => EventKind.Compaction,
                        "turn_duration" => EventKind.TurnEnd,
                        "api_error" => EventKind.Error,
                        _ => root.Str("level") == "error" ? EventKind.Error : EventKind.Context
                    },
                    NativeType = "system:" + subtype,
                    Text = root.Get("error").Str("message") ?? root.Str("content")
                };
                break;
            case "summary": yield return item with { Kind = EventKind.Compaction, Text = root.Str("summary") }; break;
            case "attachment": yield return item with { Kind = EventKind.Attachment, Text = root.Get("rendered").ContentText() }; break;
            case "file-history-delta":
                yield return item with { Kind = EventKind.FileActivity, Files = root.Str("trackingPath") is { } path ? [new FileActivity(path, "backup", true)] : [] };
                break;
            case "file-history-snapshot":
                var backups = root.Get("snapshot").Get("trackedFileBackups");
                yield return item with
                {
                    Kind = EventKind.FileActivity,
                    Timestamp = root.Get("snapshot").Get("timestamp").Time(),
                    Files = backups.ValueKind == JsonValueKind.Object ? backups.EnumerateObject().Select(x => new FileActivity(x.Name, "backup", true)).ToArray() : []
                };
                break;
            case "cost-state":
                yield return item with
                {
                    Kind = EventKind.Usage,
                    Usage = new UsageSample { Series = "claude.cost", Key = "session", Basis = UsageBasis.SessionCumulative, Cost = root.Get("totalCostUSD").Decimal(), Currency = "USD" }
                };
                if (root.Get("modelUsage").ValueKind == JsonValueKind.Object)
                {
                    foreach (var model in root.Get("modelUsage").EnumerateObject())
                    {
                        yield return item with
                        {
                            Kind = EventKind.Usage,
                            Model = model.Name,
                            NativePointer = "/modelUsage/" + JsonValue.PointerSegment(model.Name),
                            Usage = new UsageSample { Series = "claude.model_total:" + model.Name, Key = "session", Basis = UsageBasis.SessionCumulative, Tokens = UsageParsing.Tokens(model.Value), Cost = model.Value.Get("costUSD").Decimal(), Currency = "USD" }
                        };
                    }
                }

                break;
            case "custom-title":
            case "ai-title":
            case "agent-name": yield return item with { Kind = EventKind.Metadata }; break;
            default: yield return item; break;
        }
    }
}
