using System.Text.Json;
using System.Text.RegularExpressions;

using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Parsing;

internal static partial class ToolParsing
{
    internal static TranscriptEvent Call(TranscriptEvent item, string? name, string? id, JsonElement arguments,
        string? rawArguments = null) => item with
        {
            Kind = EventKind.ToolCall,
            Tool = new ToolActivity { Name = name, CallId = id, CorrelationKey = id, Arguments = rawArguments ?? arguments.Raw() },
            Text = rawArguments ?? arguments.Raw(),
            Command = Command(name, arguments),
            Files = name?.Split('.').Last() == "apply_patch" ? PatchFiles(rawArguments ?? arguments.Str("patch") ?? arguments.Str("input")) : Files(name, arguments)
        };

    internal static CommandActivity? Command(string? name, JsonElement arguments)
    {
        var shortName = name?.Split('.').Last().ToLowerInvariant();
        if (shortName is not ("bash" or "powershell" or "pwsh" or "shell" or "exec_command" or "shell_command" or "run_command"))
        {
            return null;
        }

        var command = arguments.Str("command") ?? arguments.Str("cmd") ?? arguments.Get("command").ContentText();
        return new CommandActivity { Command = command, WorkingDirectory = arguments.Str("workdir") ?? arguments.Str("cwd") };
    }

    internal static IReadOnlyList<FileActivity> Files(string? name, JsonElement arguments)
    {
        var shortName = name?.Split('.').Last().ToLowerInvariant();
        var operation = shortName switch
        {
            "read" or "read_file" => "read",
            "write" or "write_file" => "write",
            "edit" or "multiedit" or "edit_file" => "edit",
            "delete_file" => "delete",
            "glob" or "grep" => "search",
            _ => null
        };
        if (operation is null)
        {
            return [];
        }

        var path = arguments.Str("file_path") ?? arguments.Str("filePath") ?? arguments.Str("path");
        return path is null ? [] : [new FileActivity(path, operation)];
    }

    private static IReadOnlyList<FileActivity> PatchFiles(string? patch)
    {
        if (patch is null)
        {
            return [];
        }

        var files = new List<FileActivity>();
        foreach (var line in patch.Split('\n'))
        {
            foreach (var (prefix, operation) in new[] { ("*** Add File: ", "add"), ("*** Update File: ", "edit"), ("*** Delete File: ", "delete"), ("*** Move to: ", "move-target") })
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    files.Add(new FileActivity(line[prefix.Length..].TrimEnd('\r'), operation));
                    break;
                }
            }
        }
        return files;
    }

    internal static OperationOutcome Outcome(JsonElement value)
    {
        if (value.Bool("is_error") == true || value.Bool("isError") == true)
        {
            return OperationOutcome.Failed;
        }

        if (value.Bool("interrupted") == true)
        {
            return OperationOutcome.Cancelled;
        }

        return value.Str("status") switch
        {
            "error" or "failed" => OperationOutcome.Failed,
            "completed" or "success" => OperationOutcome.Succeeded,
            "cancelled" or "canceled" or "interrupted" => OperationOutcome.Cancelled,
            "running" or "pending" => OperationOutcome.Running,
            _ => value.Bool("is_error") == false || value.Bool("isError") == false ? OperationOutcome.Succeeded : OperationOutcome.Unknown
        };
    }

    internal static CommandActivity? ResultCommand(JsonElement result, string? text, AgentSource source)
    {
        var code = result.Num("exit_code") ?? result.Num("exitCode") ?? result.Num("exit") ??
            result.Get("metadata").Num("exit") ?? result.Get("metadata").Num("exitCode");
        if (code is null && source == AgentSource.Codex && text is not null)
        {
            var header = text.Split("Output:", 2, StringSplitOptions.None)[0];
            if (header.Length <= 1500)
            {
                var match = CodexExit().Match(header);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var exit))
                {
                    code = exit;
                }
            }
            if (code is null && text.StartsWith('{'))
            {
                var json = JsonValue.ParseArguments(text);
                code = json.Num("exit_code");
            }
        }
        // Only this documented job-output suffix carries a DSH process exit status.
        if (code is null && source == AgentSource.Dsh && text is not null)
        {
            var match = DshExit().Match(text);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var exit))
            {
                code = exit;
            }
        }
        if (code is < int.MinValue or > int.MaxValue)
        {
            code = null;
        }

        if (code is null && result.Get("stdout").ValueKind == JsonValueKind.Undefined && result.Get("stderr").ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return new CommandActivity
        {
            ExitCode = (int?)code,
            StandardOutput = result.Str("stdout") ?? text,
            StandardError = result.Str("stderr"),
            Outcome = code is null ? OperationOutcome.Unknown : code == 0 ? OperationOutcome.Succeeded : OperationOutcome.Failed
        };
    }

    [GeneratedRegex(@"(?m)^(?:Process exited with code|Exit code:)\s*(-?\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CodexExit();

    [GeneratedRegex(@"\[status: completed, exit code: (-?\d+)\]\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex DshExit();
}
