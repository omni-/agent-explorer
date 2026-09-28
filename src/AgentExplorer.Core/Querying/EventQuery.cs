using System.Runtime.CompilerServices;

using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Querying;

public sealed record EventQuery
{
    public IReadOnlySet<EventKind>? Kinds { get; init; }

    public string? Role { get; init; }

    public string? ToolName { get; init; }

    public string? Model { get; init; }

    public string? Provider { get; init; }

    public string? FilePath { get; init; }

    public string? Text { get; init; }

    public bool SearchNativeData { get; init; }

    public bool? IsError { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? Until { get; init; }

    public bool IncludeMirrors { get; init; } = true;

    public bool IncludeDuplicates { get; init; }

    public bool Matches(TranscriptEvent item)
    {
        if (!IncludeDuplicates && item.DuplicateOf is not null || !IncludeMirrors && item.IsMirror)
        {
            return false;
        }

        if (Kinds is not null && !Kinds.Contains(item.Kind) || Role is not null && item.Role != Role ||
            ToolName is not null && item.Tool?.Name != ToolName || Model is not null && item.Model != Model ||
            Provider is not null && item.Provider != Provider || IsError is not null && item.IsError != IsError)
        {
            return false;
        }

        if (From is not null && (item.Timestamp is null || item.Timestamp < From) || Until is not null && (item.Timestamp is null || item.Timestamp >= Until))
        {
            return false;
        }

        if (FilePath is not null && !item.Files.Any(f => f.Path.Equals(FilePath, StringComparison.Ordinal)))
        {
            return false;
        }

        return string.IsNullOrEmpty(Text) ||
            (item.Text?.Contains(Text, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (item.Command?.Command?.Contains(Text, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (item.Tool?.Name?.Contains(Text, StringComparison.OrdinalIgnoreCase) ?? false) ||
            item.Files.Any(f => f.Path.Contains(Text, StringComparison.OrdinalIgnoreCase)) ||
            SearchNativeData && item.Native.Text.Contains(Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Filters lazily, allowing callers to stop early without loading the transcript.</summary>
    public async IAsyncEnumerable<TranscriptEvent> ApplyAsync(IAsyncEnumerable<TranscriptEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (Matches(item))
            {
                yield return item;
            }
        }
    }
}
