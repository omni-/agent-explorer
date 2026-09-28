using System.Text;
using System.Text.RegularExpressions;

using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Parsing;

internal static partial class OpenCodeMarkdownParser
{
    internal static bool IsHeading(string line) => Heading().IsMatch(line);

    internal static IEnumerable<TranscriptEvent> Parse(NativeRecord native, ParsingContext context)
    {
        var lines = native.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var heading = Heading().Match(lines[0]);
        if (!heading.Success)
        {
            string? Field(string name) => lines.FirstOrDefault(l => l.StartsWith($"**{name}:**", StringComparison.Ordinal))?.Split("**", 3)[^1].Trim();
            yield return context.Event(native, EventKind.Metadata, "markdown.header") with
            {
                Metadata = new SessionMetadata { SessionId = Field("Session ID"), Title = lines.FirstOrDefault(x => x.StartsWith("# ", StringComparison.Ordinal))?[2..], FormatVersion = "markdown-export" }
            };
            yield return context.Event(native, EventKind.Diagnostic, "markdown.fidelity") with
            {
                Diagnostic = new TranscriptDiagnostic("lossy-export", "Markdown omits native IDs, usage, structured status, and absolute event timestamps. Localized header dates remain raw; tool relationships use block position.", "information")
            };
            yield break;
        }
        var role = heading.Groups[1].Value.ToLowerInvariant();
        var details = heading.Groups[2].Value.Split('·', StringSplitOptions.TrimEntries);
        var model = details.Length >= 2 ? details[1] : null;
        var baseEvent = context.Event(native, EventKind.Message, "markdown." + role) with { Role = role, Model = model, NativeId = null };
        var result = new List<TranscriptEvent>();
        var buffer = new StringBuilder();
        var reasoning = false;
        string? toolName = null;
        string? arguments = null;
        string? output = null;
        var hasInput = false;
        var hasOutput = false;
        var isError = false;
        var mode = "text";
        var toolIndex = 0;
        var fence = new MarkdownFence();

        void FlushText()
        {
            var text = buffer.ToString().Trim();
            buffer.Clear();
            if (text.Length > 0)
            {
                result.Add(baseEvent with { Kind = reasoning ? EventKind.Reasoning : EventKind.Message, Text = text });
            }
        }

        void FlushTool()
        {
            if (toolName is null)
            {
                return;
            }

            if (mode == "input")
            {
                arguments = buffer.ToString().Trim();
            }

            if (mode == "output")
            {
                output = buffer.ToString().Trim();
            }

            buffer.Clear();
            var key = $"markdown:{native.Location.RecordIndex}:{toolIndex++}";
            var call = ToolParsing.Call(baseEvent, toolName, null, JsonValue.ParseArguments(arguments), arguments);
            result.Add(call with { Tool = call.Tool! with { CorrelationKey = key } });
            if (hasOutput)
            {
                result.Add(baseEvent with { Kind = EventKind.ToolResult, Text = output, Tool = new ToolActivity { Name = toolName, CorrelationKey = key, Outcome = isError ? OperationOutcome.Failed : OperationOutcome.Unknown } });
            }

            if (hasInput && !string.IsNullOrWhiteSpace(arguments) && JsonValue.ParseArguments(arguments).ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                result.Add(baseEvent with { Kind = EventKind.Diagnostic, Diagnostic = new TranscriptDiagnostic("malformed-tool-input", "Markdown tool input is not valid JSON; the original text is preserved.") });
            }

            toolName = null;
            arguments = output = null;
            hasInput = hasOutput = false;
            isError = false;
            mode = "text";
        }

        foreach (var line in lines.Skip(1))
        {
            var wasOpen = fence.IsOpen;
            if (fence.Observe(line))
            {
                if (mode == "text")
                {
                    buffer.AppendLine(line);
                }

                continue;
            }
            if (wasOpen) { buffer.AppendLine(line); continue; }
            if (line.StartsWith("**Tool: ", StringComparison.Ordinal) && line.EndsWith("**", StringComparison.Ordinal))
            {
                if (toolName is not null)
                {
                    FlushTool();
                }
                else
                {
                    FlushText();
                }

                toolName = line[8..^2].Trim();
                reasoning = false;
                mode = "tool";
            }
            else if (toolName is not null && line == "**Input:**") { mode = "input"; hasInput = true; buffer.Clear(); }
            else if (toolName is not null && line is "**Output:**" or "**Error:**")
            {
                if (mode == "input")
                {
                    arguments = buffer.ToString().Trim();
                }

                buffer.Clear(); mode = "output"; hasOutput = true;
                isError = line == "**Error:**";
            }
            else if (line == "_Thinking:_")
            {
                if (toolName is not null)
                {
                    FlushTool();
                }
                else
                {
                    FlushText();
                }

                reasoning = true;
            }
            else if (line == "---")
            {
                if (toolName is not null)
                {
                    FlushTool();
                }
                else
                {
                    FlushText();
                }

                reasoning = false;
            }
            else
            {
                buffer.AppendLine(line);
            }
        }
        if (toolName is not null)
        {
            FlushTool();
        }
        else
        {
            FlushText();
        }

        if (fence.IsOpen)
        {
            result.Add(baseEvent with { Kind = EventKind.Diagnostic, Diagnostic = new TranscriptDiagnostic("truncated-markdown", "Unclosed code fence in the exported section.") });
        }

        if (result.Count == 0)
        {
            result.Add(baseEvent);
        }

        foreach (var item in result)
        {
            yield return item;
        }
    }

    [GeneratedRegex(@"^## (User|Assistant|System)(?: \((.*)\))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Heading();
}
