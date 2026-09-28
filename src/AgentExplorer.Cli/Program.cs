using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AgentExplorer.Core;
using AgentExplorer.Core.Analysis;
using AgentExplorer.Core.Discovery;
using AgentExplorer.Core.Models;
using AgentExplorer.Core.Querying;

var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    if (args.Length == 0 || args[0] is "--help" or "help")
    {
        Console.WriteLine("Agent Explorer diagnostic CLI\n  discover [export-directory ...]\n  stats <path> [format] [zip-entry]\n  events <path> [search-text]\n  verify <path> [format] [zip-entry]\n\nFormats: CodexJsonl, ClaudeCodeJsonl, OpenCodeJson, OpenCodeMarkdown, DshJsonl\nNo commands from transcripts are executed. events emits JSONL and includes raw data.");
        return 0;
    }
    if (args[0] == "discover")
    {
        var discovery = await new SessionDiscovery().DiscoverAsync(new DiscoveryOptions { ExportDirectories = args.Skip(1).ToArray() }, cancellation.Token);
        Console.WriteLine(JsonSerializer.Serialize(discovery, jsonOptions));
        return 0;
    }
    if (args.Length < 2)
    {
        throw new ArgumentException("A transcript path is required.");
    }

    var reader = new TranscriptReader();
    var format = args[0] != "events" && args.Length > 2 ? Enum.Parse<TranscriptFormat>(args[2], true) : (TranscriptFormat?)null;
    var input = new TranscriptInput(args[1], format, args.Length > 3 ? args[3] : null);
    if (args[0] == "events")
    {
        var query = new EventQuery { Text = args.Length > 2 ? args[2] : null };
        var compact = new JsonSerializerOptions(jsonOptions) { WriteIndented = false };
        await foreach (var item in query.ApplyAsync(reader.ReadEventsAsync(input, cancellationToken: cancellation.Token), cancellation.Token))
        {
            Console.WriteLine(JsonSerializer.Serialize(item, compact));
        }

        return 0;
    }
    if (args[0] is not ("stats" or "verify"))
    {
        throw new ArgumentException("Unknown diagnostic command.");
    }

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var builder = new SessionStatisticsBuilder();
    var warnings = 0;
    using var rawHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    long previousRecord = -1;
    await foreach (var item in reader.ReadEventsAsync(input, cancellationToken: cancellation.Token))
    {
        builder.Add(item);
        if (item.Diagnostic is { Severity: not "information" })
        {
            warnings++;
        }

        if (item.Native.Location.RecordIndex != previousRecord)
        {
            rawHash.AppendData(Encoding.UTF8.GetBytes(item.Native.Text));
            previousRecord = item.Native.Location.RecordIndex;
        }
    }
    var statistics = builder.Build();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Format = await reader.DetectFormatAsync(input, cancellation.Token),
        Statistics = statistics,
        NativeRecordsSha256 = Convert.ToHexStringLower(rawHash.GetHashAndReset()),
        ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
    }, jsonOptions));
    return args[0] == "verify" && warnings > 0 ? 2 : 0;
}
catch (OperationCanceledException) { return 130; }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
