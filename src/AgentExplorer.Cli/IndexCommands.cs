using System.Globalization;
using System.Text.Json;

using AgentExplorer.Core.Discovery;
using AgentExplorer.Core.Indexing;
using AgentExplorer.Core.Models;
using AgentExplorer.Core.Querying;

namespace AgentExplorer.Cli;

internal static class IndexCommands
{
    internal static async Task<int> RunAsync(string[] args, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
        {
            throw new ArgumentException("An index database path is required.");
        }
        var index = new LocalTranscriptIndex(args[1]);
        if (args[0] == "index-scan")
        {
            var result = await index.RescanAsync(new DiscoveryOptions
            {
                UseDefaultLocations = args.Length == 2,
                ExportDirectories = args.Skip(2).ToArray()
            }, cancellationToken);
            Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
            return result.Issues.Count == 0 ? 0 : 2;
        }
        if (args[0] is not ("index-inputs" or "index-events"))
        {
            throw new ArgumentException("Unknown index command.");
        }

        var inputs = new IndexInputQuery();
        var events = new EventQuery();
        var pageSize = 100;
        string? cursor = null;
        for (var i = 2; i < args.Length; i++)
        {
            var option = args[i];
            switch (option)
            {
                case "--source": inputs = inputs with { Source = ParseEnum<AgentSource>(Value()) }; break;
                case "--session": inputs = inputs with { SessionId = Value() }; break;
                case "--workspace": inputs = inputs with { Workspace = Value() }; break;
                case "--input": inputs = inputs with { InputId = Value() }; break;
                case "--page-size": pageSize = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--cursor": cursor = Value(); break;
                case "--text" when args[0] == "index-events": events = events with { Text = Value() }; break;
                case "--kind" when args[0] == "index-events":
                    events = events with { Kinds = Value().Split(',').Select(ParseEnum<EventKind>).ToHashSet() };
                    break;
                case "--role" when args[0] == "index-events": events = events with { Role = Value() }; break;
                case "--tool" when args[0] == "index-events": events = events with { ToolName = Value() }; break;
                case "--model" when args[0] == "index-events": events = events with { Model = Value() }; break;
                case "--provider" when args[0] == "index-events": events = events with { Provider = Value() }; break;
                case "--file" when args[0] == "index-events": events = events with { FilePath = Value() }; break;
                case "--from" when args[0] == "index-events": events = events with { From = ParseTime(Value()) }; break;
                case "--until" when args[0] == "index-events": events = events with { Until = ParseTime(Value()) }; break;
                case "--errors" when args[0] == "index-events": events = events with { IsError = true }; break;
                case "--native" when args[0] == "index-events": events = events with { SearchNativeData = true }; break;
                case "--duplicates" when args[0] == "index-events": events = events with { IncludeDuplicates = true }; break;
                case "--no-mirrors" when args[0] == "index-events": events = events with { IncludeMirrors = false }; break;
                default: throw new ArgumentException($"Unknown option: {option}");
            }

            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"A value is required after {option}.");
        }

        var json = args[0] == "index-inputs"
            ? JsonSerializer.Serialize(index.QueryInputs(inputs, pageSize, cursor, cancellationToken), jsonOptions)
            : JsonSerializer.Serialize(index.QueryEvents(events, inputs, pageSize, cursor, cancellationToken), jsonOptions);
        Console.WriteLine(json);
        return 0;
    }

    private static T ParseEnum<T>(string value) where T : struct, Enum => Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed)
        ? parsed : throw new ArgumentException($"Invalid {typeof(T).Name}: {value}");

    private static DateTimeOffset ParseTime(string value)
    {
        if (!value.EndsWith('Z') && !(value.Length >= 6 && value[^3] == ':' && value[^6] is '+' or '-'))
        {
            throw new ArgumentException("Timestamps require an explicit Z or UTC offset.");
        }
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }
}
