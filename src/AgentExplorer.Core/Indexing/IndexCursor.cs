using System.Security.Cryptography;
using System.Text.Json;

using AgentExplorer.Core.Querying;

namespace AgentExplorer.Core.Indexing;

internal sealed record IndexCursor(string IndexRevision, string QueryHash, string InputId, long Sequence)
{
    internal string Encode() => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this));

    internal static string QueryHashFor(string kind, IndexInputQuery inputs, EventQuery? events = null) =>
        Hash(new { kind, inputs, events = events is null ? null : events with { Kinds = events.Kinds is null ? null : new SortedSet<Models.EventKind>(events.Kinds) } });

    internal static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    internal static IndexCursor? Read(string? token, string revision, string queryHash, int pageSize)
    {
        if (pageSize is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be between 1 and 1000.");
        }
        if (token is null)
        {
            return null;
        }

        IndexCursor? cursor;
        try
        {
            if (token.Length > 4096)
            {
                throw new FormatException();
            }

            cursor = JsonSerializer.Deserialize<IndexCursor>(Convert.FromBase64String(token));
            if (cursor is null || cursor.InputId is null || cursor.InputId.Length != 64 || cursor.Sequence < -1 ||
                cursor.QueryHash != queryHash || string.IsNullOrEmpty(cursor.IndexRevision))
            {
                throw new FormatException();
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("Invalid continuation token or different query filters.", nameof(token), ex);
        }

        if (cursor.IndexRevision != revision)
        {
            throw new StaleIndexCursorException();
        }
        return cursor;
    }
}
