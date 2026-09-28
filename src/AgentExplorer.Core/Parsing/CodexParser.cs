using System.Text.Json;

using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Parsing;

internal static class CodexParser
{
    internal static IEnumerable<TranscriptEvent> Parse(NativeRecord native, JsonElement root, ParsingContext context)
    {
        var type = root.Str("type") ?? "unknown";
        var payload = root.Get("payload");
        var subtype = payload.Str("type") ?? type;
        var time = root.Get("timestamp").Time();
        context.Turn = payload.Str("turn_id") ?? payload.Get("internal_chat_message_metadata_passthrough").Str("turn_id") ?? context.Turn;
        if (type is "session_meta" or "turn_context")
        {
            context.Model = payload.Str("model") ?? context.Model;
            context.Provider = payload.Str("model_provider") ?? context.Provider;
            yield return context.Event(native, EventKind.Metadata, type, time, "/payload") with
            {
                Metadata = new SessionMetadata
                {
                    SessionId = payload.Str("session_id") ?? (type == "session_meta" ? payload.Str("id") : null),
                    ParentSessionId = payload.Str("forked_from_id"),
                    Workspace = payload.Str("cwd"),
                    Model = payload.Str("model"),
                    Provider = payload.Str("model_provider"),
                    AgentVersion = payload.Str("cli_version"),
                    CreatedAt = type == "session_meta" ? payload.Get("timestamp").Time() : null
                }
            };
            yield break;
        }
        var item = context.Event(native, EventKind.Unknown, type + ":" + subtype, time, "/payload") with { NativeId = payload.Str("id") };
        if (type == "response_item")
        {
            item = item with { Layer = ActivityLayer.Model, MessageId = payload.Str("id") };
            switch (subtype)
            {
                case "message":
                    yield return item with { Kind = EventKind.Message, Role = payload.Str("role"), Text = payload.Get("content").ContentText() };
                    break;
                case "reasoning":
                    yield return item with { Kind = EventKind.Reasoning, Role = "assistant", Text = payload.Get("summary").ContentText() ?? payload.Get("content").ContentText() };
                    break;
                case "function_call":
                case "custom_tool_call":
                    var args = payload.Str("arguments") ?? payload.Str("input");
                    var name = payload.Str("name");
                    if (payload.Str("namespace") is { } ns)
                    {
                        name = ns + "." + name;
                    }

                    yield return ToolParsing.Call(item, name, payload.Str("call_id"), JsonValue.ParseArguments(args), args);
                    break;
                case "function_call_output":
                case "custom_tool_call_output":
                    var output = payload.Get("output").ContentText();
                    yield return item with
                    {
                        Kind = EventKind.ToolResult,
                        Text = output,
                        Tool = new ToolActivity { CallId = payload.Str("call_id"), CorrelationKey = payload.Str("call_id"), Outcome = ToolParsing.Outcome(payload) },
                        Command = ToolParsing.ResultCommand(payload, output, AgentSource.Codex)
                    };
                    break;
                default: yield return item; break;
            }
            yield break;
        }
        if (type == "token_usage_record")
        {
            yield return item with
            {
                Kind = EventKind.Usage,
                Usage = new UsageSample
                {
                    Series = "codex.response",
                    Key = payload.Str("response_id") ?? "record:" + native.Location.RecordIndex,
                    Basis = UsageBasis.Response,
                    Tokens = UsageParsing.Tokens(payload.Get("usage"))
                }
            };
            if (payload.Get("thread_token_usage").ValueKind == JsonValueKind.Object)
            {
                yield return item with
                {
                    Kind = EventKind.Usage,
                    Usage = new UsageSample { Series = "codex.thread", Key = "session", Basis = UsageBasis.SessionCumulative, Tokens = UsageParsing.Tokens(payload.Get("thread_token_usage")) }
                };
            }

            yield break;
        }
        if (type == "compacted")
        {
            yield return item with { Kind = EventKind.Compaction, Text = payload.Str("message") };
            yield break;
        }
        if (type == "world_state")
        {
            context.Model = payload.Get("state").Str("model") ?? context.Model;
            yield return item with { Kind = EventKind.Context, Model = context.Model };
            yield break;
        }
        if (type != "event_msg") { yield return item; yield break; }
        switch (subtype)
        {
            case "task_started": yield return item with { Kind = EventKind.TurnStart }; break;
            case "task_complete":
            case "task_completed":
            case "turn_aborted": yield return item with { Kind = EventKind.TurnEnd }; break;
            case "error": yield return item with { Kind = EventKind.Error, Text = payload.Str("message") ?? payload.Str("error") }; break;
            case "user_message":
            case "agent_message":
                yield return item with { Kind = EventKind.Message, Role = subtype == "user_message" ? "user" : "assistant", Text = payload.Str("message"), IsMirror = true };
                break;
            case "agent_reasoning":
            case "agent_reasoning_raw_content":
                yield return item with { Kind = EventKind.Reasoning, Role = "assistant", Text = payload.Str("text"), IsMirror = true };
                break;
            case "context_compacted": yield return item with { Kind = EventKind.Compaction, IsMirror = true }; break;
            case "token_count":
                var info = payload.Get("info");
                yield return item with
                {
                    Kind = EventKind.Usage,
                    Usage = new UsageSample
                    {
                        Series = "codex.token_count",
                        Key = "session",
                        Basis = UsageBasis.SessionCumulative,
                        Tokens = UsageParsing.Tokens(info.Get("total_token_usage")),
                        ContextWindow = info.Num("model_context_window")
                    }
                };
                break;
            case "thread_settings_applied":
                var settings = payload.Get("thread_settings");
                context.Model = settings.Str("model") ?? context.Model;
                context.Provider = settings.Str("model_provider_id") ?? context.Provider;
                yield return item with
                {
                    Kind = EventKind.Metadata,
                    Model = context.Model,
                    Provider = context.Provider,
                    Metadata = new SessionMetadata { Model = settings.Str("model"), Provider = settings.Str("model_provider_id"), Workspace = settings.Str("cwd") }
                };
                break;
            case "item_completed":
                foreach (var expanded in Completed(item, payload.Get("item"), payload))
                {
                    yield return expanded;
                }

                break;
            default: yield return item; break;
        }
    }

    private static IEnumerable<TranscriptEvent> Completed(TranscriptEvent original, JsonElement value, JsonElement payload)
    {
        var type = value.Str("type");
        var item = original with { NativeType = "event_msg:item_completed:" + type, NativeId = value.Str("id"), NativePointer = "/payload/item", Layer = ActivityLayer.Execution };
        switch (type)
        {
            case "UserMessage":
            case "AgentMessage":
                yield return item with { Kind = EventKind.Message, Role = type == "UserMessage" ? "user" : "assistant", Text = value.Get("content").ContentText(), IsMirror = true };
                break;
            case "Reasoning":
                yield return item with { Kind = EventKind.Reasoning, Text = value.Get("summary_text").ContentText() ?? value.Get("raw_content").ContentText(), Role = "assistant", IsMirror = true };
                break;
            case "ContextCompaction": yield return item with { Kind = EventKind.Compaction, IsMirror = true }; break;
            case "CommandExecution":
                var command = value.Get("command");
                var result = ToolParsing.ResultCommand(value, value.Str("aggregated_output"), AgentSource.Codex) ?? new CommandActivity();
                yield return item with
                {
                    Kind = EventKind.Command,
                    Timestamp = payload.Get("completed_at_ms").Time() ?? original.Timestamp,
                    Text = value.Str("aggregated_output"),
                    Command = result with
                    {
                        Command = command.ValueKind == JsonValueKind.Array ? string.Join(" ", command.Items().Select(x => x.String())) : command.String(),
                        WorkingDirectory = value.Str("cwd"),
                        ProcessId = value.Str("process_id")
                    }
                };
                break;
            case "FileChange":
                var files = value.Get("changes").ValueKind == JsonValueKind.Object
                    ? value.Get("changes").EnumerateObject().Select(x => new FileActivity(x.Name, x.Value.Str("type") ?? "change", true)).ToArray() : [];
                yield return item with { Kind = EventKind.FileActivity, Text = value.Str("stdout"), Files = files };
                if (ToolParsing.Outcome(value) == OperationOutcome.Failed)
                {
                    yield return item with { Kind = EventKind.Error, Text = value.Str("stderr") };
                }

                break;
            case "McpToolCall":
                var name = value.Str("server") + "." + value.Str("tool");
                var call = ToolParsing.Call(item, name, value.Str("id"), value.Get("arguments"));
                yield return call with { Timestamp = payload.Get("started_at_ms").Time() ?? original.Timestamp };
                if (value.Get("result").ValueKind != JsonValueKind.Undefined || value.Get("error").ValueKind != JsonValueKind.Undefined)
                {
                    yield return item with
                    {
                        Kind = EventKind.ToolResult,
                        Text = value.Get("result").Get("content").ContentText() ?? value.Get("error").ContentText(),
                        Tool = new ToolActivity { Name = name, CallId = value.Str("id"), CorrelationKey = value.Str("id"), Outcome = value.Get("error").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) ? OperationOutcome.Failed : ToolParsing.Outcome(value.Get("result")) }
                    };
                }

                break;
            default: yield return item; break;
        }
    }
}
