using System.Text.Json;

using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Parsing;

internal static class OpenCodeParser
{
    internal static IEnumerable<TranscriptEvent> Parse(NativeRecord native, JsonElement root, ParsingContext context)
    {
        if (native.Location.JsonPointer == "/info")
        {
            context.Model = root.Get("model").Str("id") ?? root.Get("model").Str("modelID");
            context.Provider = root.Get("model").Str("providerID");
            var item = context.Event(native, EventKind.Metadata, "session.info", root.Get("time").Get("created").Time());
            yield return item with
            {
                Metadata = new SessionMetadata
                {
                    SessionId = root.Str("id"),
                    ParentSessionId = root.Str("parentID"),
                    Title = root.Str("title"),
                    Workspace = root.Str("directory") ?? root.Get("location").Str("directory"),
                    ProjectId = root.Str("projectID"),
                    Model = context.Model,
                    Provider = context.Provider,
                    AgentVersion = root.Str("version"),
                    CreatedAt = root.Get("time").Get("created").Time(),
                    UpdatedAt = root.Get("time").Get("updated").Time()
                }
            };
            if (root.Get("tokens").ValueKind == JsonValueKind.Object || root.Get("cost").ValueKind == JsonValueKind.Number)
            {
                yield return item with { Kind = EventKind.Usage, Timestamp = root.Get("time").Get("updated").Time(), Usage = Sample(root, "opencode.session", "session", UsageBasis.SessionCumulative) };
            }

            yield break;
        }
        if (native.Location.JsonPointer?.StartsWith("/messages/", StringComparison.Ordinal) != true)
        {
            yield return context.Event(native, EventKind.Unknown, "export.property");
            yield break;
        }
        var v1 = root.Get("info").ValueKind == JsonValueKind.Object;
        var info = v1 ? root.Get("info") : root;
        var id = info.Str("id");
        var role = info.Str("role") ?? info.Str("type");
        var model = info.Str("modelID") ?? info.Get("model").Str("modelID") ?? info.Get("model").Str("id");
        var provider = info.Str("providerID") ?? info.Get("model").Str("providerID");
        var itemBase = context.Event(native, EventKind.Message, v1 ? "v1.message" : "v2.message", info.Get("time").Get("created").Time(), v1 ? "/info" : "") with
        {
            NativeId = id,
            MessageId = id,
            Role = role,
            Model = model,
            Provider = provider,
            Metadata = new SessionMetadata { FormatVersion = v1 ? "v1-export" : "v2-export", Model = model, Provider = provider, Workspace = info.Get("path").Str("cwd") }
        };
        var parts = root.Get(v1 ? "parts" : "content");
        var hasMessageUsage = info.Get("tokens").ValueKind == JsonValueKind.Object;
        if (info.Str("text") is { } text)
        {
            yield return itemBase with { Text = text };
        }

        if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0)
        {
            if (info.Str("text") is null)
            {
                yield return itemBase;
            }
        }
        var index = 0;
        foreach (var part in parts.Items())
        {
            var type = part.Str("type") ?? "unknown";
            var time = part.Get("time");
            var start = time.Get(v1 ? "start" : "created").Time();
            var item = itemBase with { NativeType = (v1 ? "v1.part:" : "v2.content:") + type, NativePointer = $"/{(v1 ? "parts" : "content")}/{index++}", Timestamp = start ?? itemBase.Timestamp };
            switch (type)
            {
                case "text": yield return item with { Kind = EventKind.Message, Text = part.Str("text") }; break;
                case "reasoning": yield return item with { Kind = EventKind.Reasoning, Text = part.Str("text") }; break;
                case "tool":
                    var state = part.Get("state");
                    var callId = part.Str("callID") ?? part.Str("id");
                    var name = part.Str("tool") ?? part.Str("name");
                    var call = ToolParsing.Call(item, name, callId, state.Get("input"));
                    yield return call with { Timestamp = state.Get("time").Get("start").Time() ?? start ?? itemBase.Timestamp };
                    var outcome = ToolParsing.Outcome(state);
                    if (outcome is OperationOutcome.Succeeded or OperationOutcome.Failed or OperationOutcome.Cancelled ||
                        state.Get("output").ValueKind != JsonValueKind.Undefined || state.Get("content").ValueKind != JsonValueKind.Undefined)
                    {
                        var output = state.Str("output") ?? state.Get("content").ContentText() ?? state.Str("error");
                        yield return item with
                        {
                            Kind = EventKind.ToolResult,
                            Text = output,
                            Timestamp = state.Get("time").Get("end").Time() ?? time.Get("completed").Time() ?? itemBase.Timestamp,
                            Tool = new ToolActivity { Name = name, CallId = callId, CorrelationKey = callId, Outcome = outcome },
                            Command = ToolParsing.Command(name, state.Get("input")) is not null ? ToolParsing.ResultCommand(state, output, AgentSource.OpenCode) : null
                        };
                    }
                    break;
                case "step-start": yield return item with { Kind = EventKind.StepStart, StepId = part.Str("id") }; break;
                case "step-finish":
                    yield return item with { Kind = EventKind.StepEnd, StepId = part.Str("id") };
                    if (part.Get("tokens").ValueKind == JsonValueKind.Object)
                    {
                        yield return item with { Kind = EventKind.Usage, IsMirror = hasMessageUsage, Usage = Sample(part, "opencode.step", part.Str("id") ?? $"{id}:{index}", UsageBasis.Step) };
                    }

                    break;
                case "compaction": yield return item with { Kind = EventKind.Compaction }; break;
                case "patch":
                    yield return item with { Kind = EventKind.FileActivity, Files = part.Get("files").Items().Where(x => x.String() is not null).Select(x => new FileActivity(x.String()!, "change", true)).ToArray() };
                    break;
                case "file": yield return item with { Kind = EventKind.Attachment }; break;
                default: yield return item with { Kind = EventKind.Unknown }; break;
            }
        }
        if (hasMessageUsage)
        {
            yield return itemBase with
            {
                Kind = EventKind.Usage,
                NativePointer = v1 ? "/info/tokens" : "/tokens",
                Timestamp = info.Get("time").Get("completed").Time() ?? itemBase.Timestamp,
                Usage = Sample(info, "opencode.response", id ?? "record:" + native.Location.RecordIndex, UsageBasis.Response)
            };
        }

        if (info.Get("error").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            yield return itemBase with { Kind = EventKind.Error, NativePointer = v1 ? "/info/error" : "/error", Text = info.Get("error").Get("data").Str("message") ?? info.Get("error").Str("message") };
        }
    }

    private static UsageSample Sample(JsonElement data, string series, string key, UsageBasis basis) => new()
    {
        Series = series,
        Key = key,
        Basis = basis,
        Tokens = UsageParsing.Tokens(data.Get("tokens")),
        Cost = data.Get("cost").Decimal(),
        Currency = data.Get("cost").ValueKind == JsonValueKind.Number ? "USD" : null
    };
}
