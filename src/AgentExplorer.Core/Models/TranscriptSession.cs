using AgentExplorer.Core.Analysis;
using AgentExplorer.Core.Querying;

namespace AgentExplorer.Core.Models;

public sealed class TranscriptSession
{
    public TranscriptInput Input { get; }
    public SessionMetadata Metadata { get; }
    public IReadOnlyList<TranscriptEvent> Events { get; }
    public IReadOnlyList<NativeRecord> NativeRecords { get; }
    public IReadOnlyList<ToolInvocation> Tools { get; }
    public SessionStatistics Statistics { get; }

    internal TranscriptSession(TranscriptInput input, List<TranscriptEvent> events, bool orderByTimestamp)
    {
        Input = input;
        NativeRecords = events.DistinctBy(x => x.Native.Location.RecordIndex).Select(x => x.Native).ToArray();
        var metadata = new SessionMetadata();
        var statistics = new SessionStatisticsBuilder();
        foreach (var item in events)
        {
            statistics.Add(item);
            if (item.DuplicateOf is null && item.Metadata is { } update)
            {
                metadata = MetadataMerge.Apply(metadata, update);
            }
        }
        Metadata = metadata;
        Statistics = statistics.Build();
        Tools = events.Where(x => x.Kind is EventKind.ToolCall or EventKind.ToolResult && !x.IsMirror && x.DuplicateOf is null && x.Tool?.CorrelationKey is not null)
            .GroupBy(x => x.Tool!.CorrelationKey!, StringComparer.Ordinal)
            .Select(g => new ToolInvocation(g.Key, g.Where(x => x.Kind == EventKind.ToolCall).ToArray(), g.Where(x => x.Kind == EventKind.ToolResult).ToArray())).ToArray();
        if (orderByTimestamp)
        {
            // Unknown times stay attached to the preceding source record for placement only.
            // Timestamp itself remains null; equal keys keep their source sequence.
            var anchor = DateTimeOffset.MinValue;
            Events = events.Select(e => { anchor = e.Timestamp ?? anchor; return (Event: e, Anchor: anchor); })
                .OrderBy(x => x.Anchor).ThenBy(x => x.Event.Sequence).Select(x => x.Event).ToArray();
        }
        else
        {
            Events = events.AsReadOnly();
        }
    }

    public IEnumerable<TranscriptEvent> Query(EventQuery query) => Events.Where(query.Matches);
}
