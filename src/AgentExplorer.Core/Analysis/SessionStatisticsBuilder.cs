using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Analysis;

/// <summary>Incremental facts; retains identifiers and counters rather than message bodies. Feed native order.</summary>
public sealed class SessionStatisticsBuilder
{
    private readonly HashSet<long> _records = [];
    private readonly Dictionary<EventKind, long> _counts = [];
    private readonly Dictionary<string, long> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<ActivityLayer, Dictionary<string, long>> _toolsByLayer = [];
    private readonly Dictionary<string, (int Calls, int Results, bool Shell, int? Exit)> _links = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Name, string Hash), long> _repeated = [];
    private readonly HashSet<string> _requested = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly HashSet<string> _models = new(StringComparer.Ordinal);
    private readonly HashSet<string> _providers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _userMessages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _assistantMessages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, UsageSample>> _usage = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _regressions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long? Window, long? Tokens)> _contextMaxima = new(StringComparer.Ordinal);
    private long _events;
    private long _duplicates;
    private long _mirrors;
    private long _diagnostics;
    private long _errors;
    private long _turnStarts;
    private long _turnEnds;
    private long _timestampCount;
    private long _unkeyedCalls;
    private long _unkeyedResults;
    private long _executionCommands;
    private long _executionSuccesses;
    private long _executionFailures;
    private DateTimeOffset? _first;
    private DateTimeOffset? _last;

    public void Add(TranscriptEvent item)
    {
        _events++;
        _records.Add(item.Native.Location.RecordIndex);
        if (item.Timestamp is { } time)
        {
            _timestampCount++;
            _first = _first is null || time < _first ? time : _first;
            _last = _last is null || time > _last ? time : _last;
        }
        if (item.Model is { } model)
        {
            _models.Add(model);
        }

        if (item.Provider is { } provider)
        {
            _providers.Add(provider);
        }

        if (item.DuplicateOf is not null) { _duplicates++; return; }
        if (!item.IsMirror || item.Source == AgentSource.Dsh && item.MessageId is not null)
        {
            if (item.Kind is EventKind.Message or EventKind.Reasoning or EventKind.Attachment or EventKind.ToolCall)
            {
                var message = item.MessageId ?? item.NativeId ?? "record:" + item.Native.Location.RecordIndex;
                if (item.Role == "user")
                {
                    _userMessages.Add(message);
                }

                if (item.Role == "assistant")
                {
                    _assistantMessages.Add(message);
                }
            }
        }
        if (item.IsMirror) { _mirrors++; return; }
        Increment(_counts, item.Kind);
        if (item.Diagnostic is not null)
        {
            _diagnostics++;
        }

        if (item.IsError)
        {
            _errors++;
        }

        if (item.Kind == EventKind.TurnStart)
        {
            _turnStarts++;
        }

        if (item.Kind == EventKind.TurnEnd)
        {
            _turnEnds++;
        }

        foreach (var file in item.Files)
        {
            (file.IsReported ? _reported : _requested).Add(file.Path);
        }

        if (item.Kind == EventKind.Command && item.Command is { } execution)
        {
            _executionCommands++;
            if (execution.ExitCode == 0)
            {
                _executionSuccesses++;
            }

            if (execution.ExitCode is not null and not 0)
            {
                _executionFailures++;
            }
        }
        if (item.Kind is EventKind.ToolCall or EventKind.ToolResult && item.Tool is { } tool)
        {
            if (item.Kind == EventKind.ToolCall)
            {
                var name = tool.Name ?? "(unknown)";
                Increment(_tools, name);
                if (!_toolsByLayer.TryGetValue(item.Layer, out var layer))
                {
                    _toolsByLayer[item.Layer] = layer = new(StringComparer.Ordinal);
                }

                Increment(layer, name);
                Increment(_repeated, (name, ArgumentsHash(tool.Arguments)));
            }
            if (tool.CorrelationKey is { } key)
            {
                var link = _links.GetValueOrDefault(key);
                _links[key] = item.Kind == EventKind.ToolCall
                    ? (link.Calls + 1, link.Results, link.Shell || item.Command is not null, link.Exit)
                    : (link.Calls, link.Results + 1, link.Shell, item.Command?.ExitCode ?? link.Exit);
            }
            else if (item.Kind == EventKind.ToolCall)
            {
                _unkeyedCalls++;
            }
            else
            {
                _unkeyedResults++;
            }
        }
        if (item.Usage is { } usage)
        {
            if (!_usage.TryGetValue(usage.Series, out var series))
            {
                _usage[usage.Series] = series = new(StringComparer.Ordinal);
            }

            if (usage.Basis == UsageBasis.SessionCumulative && series.TryGetValue(usage.Key, out var previous) && Regressed(previous.Tokens, usage.Tokens))
            {
                Increment(_regressions, usage.Series);
            }
            // A null token_count record is an availability notice, not a reset to zero or null.
            if (!series.TryGetValue(usage.Key, out var old) || HasCounters(usage) || !HasCounters(old))
            {
                series[usage.Key] = usage;
            }

            var maximum = _contextMaxima.GetValueOrDefault(usage.Series);
            _contextMaxima[usage.Series] = (Max(maximum.Window, usage.ContextWindow), Max(maximum.Tokens, usage.ContextTokens));
        }
    }

    public SessionStatistics Build()
    {
        var shell = _links.Values.Where(x => x.Calls == 1 && x.Shell).ToArray();
        var successful = shell.LongCount(x => x.Exit == 0);
        var failed = shell.LongCount(x => x.Exit is not null and not 0);
        return new SessionStatistics
        {
            NativeRecords = _records.Count,
            Events = _events,
            DuplicateEvents = _duplicates,
            MirrorEvents = _mirrors,
            DiagnosticEvents = _diagnostics,
            FirstTimestamp = _first,
            LastTimestamp = _last,
            ObservedDuration = _timestampCount >= 2 ? _last - _first : null,
            TurnCount = _turnStarts > 0 ? _turnStarts : null,
            TurnEndCount = _turnEnds,
            UserMessageCount = _userMessages.Count,
            AssistantMessageCount = _assistantMessages.Count,
            Errors = _errors,
            Compactions = _counts.GetValueOrDefault(EventKind.Compaction),
            CallsWithoutResults = _unkeyedCalls + _links.Values.Where(x => x.Results == 0).Sum(x => (long)x.Calls),
            ResultsWithoutCalls = _unkeyedResults + _links.Values.Where(x => x.Calls == 0).Sum(x => (long)x.Results),
            AmbiguousToolKeys = _links.Values.LongCount(x => x.Calls > 1),
            EventCounts = new SortedDictionary<EventKind, long>(_counts),
            ToolUsage = new SortedDictionary<string, long>(_tools, StringComparer.Ordinal),
            ToolUsageByLayer = _toolsByLayer.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => (IReadOnlyDictionary<string, long>)new SortedDictionary<string, long>(x.Value, StringComparer.Ordinal)),
            Commands = new SortedDictionary<string, CommandStatistics>(StringComparer.Ordinal)
            {
                ["execution-events"] = new(_executionCommands, _executionSuccesses, _executionFailures, _executionCommands - _executionSuccesses - _executionFailures),
                ["shell-tools"] = new(shell.Length, successful, failed, shell.Length - successful - failed)
            },
            RequestedFiles = _requested.Order(StringComparer.Ordinal).ToArray(),
            ReportedFiles = _reported.Order(StringComparer.Ordinal).ToArray(),
            RepeatedTools = _repeated.Where(x => x.Value > 1).OrderBy(x => x.Key.Name, StringComparer.Ordinal).ThenBy(x => x.Key.Hash, StringComparer.Ordinal)
                .Select(x => new RepeatedToolActivity(x.Key.Name, x.Key.Hash, x.Value)).ToArray(),
            Usage = _usage.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => Aggregate(x.Key, x.Value.Values)).ToArray(),
            Models = _models.Order(StringComparer.Ordinal).ToArray(),
            Providers = _providers.Order(StringComparer.Ordinal).ToArray()
        };
    }

    public static async Task<SessionStatistics> CalculateAsync(IAsyncEnumerable<TranscriptEvent> events, CancellationToken cancellationToken = default)
    {
        var builder = new SessionStatisticsBuilder();
        await foreach (var item in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            builder.Add(item);
        }

        return builder.Build();
    }

    private UsageStatistics Aggregate(string series, IEnumerable<UsageSample> values)
    {
        var samples = values.ToArray();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        long? Sum(string name, Func<TokenUsage, long?> select)
        {
            var counters = samples.Select(x => select(x.Tokens)).Where(x => x is not null).ToArray();
            counts[name] = counters.Length;
            return counters.Length == 0 ? null : counters.Sum(x => x!.Value);
        }
        var tokens = new TokenUsage
        {
            Input = Sum("input", x => x.Input),
            Output = Sum("output", x => x.Output),
            Total = Sum("total", x => x.Total),
            CacheRead = Sum("cacheRead", x => x.CacheRead),
            CacheWrite = Sum("cacheWrite", x => x.CacheWrite),
            Reasoning = Sum("reasoning", x => x.Reasoning)
        };
        var costs = samples.Where(x => x.Cost is not null).ToArray();
        var maximum = _contextMaxima[series];
        return new UsageStatistics
        {
            Series = series,
            Basis = samples[0].Basis,
            Samples = samples.Length,
            Tokens = tokens,
            KnownCounterSamples = counts,
            Cost = costs.Length == 0 ? null : costs.Sum(x => x.Cost),
            Currency = costs.Select(x => x.Currency).Distinct().Count() == 1 ? costs[0].Currency : null,
            MaximumContextWindow = maximum.Window,
            MaximumContextTokens = maximum.Tokens,
            CounterRegressions = _regressions.GetValueOrDefault(series)
        };
    }

    private static bool HasCounters(UsageSample sample) => sample.Cost is not null || sample.ContextWindow is not null || sample.ContextTokens is not null ||
        sample.Tokens is { Input: not null } or { Output: not null } or { Total: not null } or { CacheRead: not null } or { CacheWrite: not null } or { Reasoning: not null };

    private static bool Regressed(TokenUsage old, TokenUsage current) => current.Input < old.Input || current.Output < old.Output || current.Total < old.Total;

    private static long? Max(long? left, long? right) => left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);

    private static void Increment<TKey>(Dictionary<TKey, long> dictionary, TKey key) where TKey : notnull => dictionary[key] = dictionary.GetValueOrDefault(key) + 1;

    private static void Increment<TKey>(Dictionary<TKey, int> dictionary, TKey key) where TKey : notnull => dictionary[key] = dictionary.GetValueOrDefault(key) + 1;

    private static string ArgumentsHash(string? arguments)
    {
        if (arguments is null)
        {
            return "unknown";
        }

        try
        {
            using var doc = JsonDocument.Parse(arguments);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteCanonical(writer, doc.RootElement);
            }

            return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
        }
        catch (JsonException) { return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(arguments))); }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var child in element.EnumerateArray())
            {
                WriteCanonical(writer, child);
            }

            writer.WriteEndArray();
        }
        else
        {
            element.WriteTo(writer);
        }
    }
}
