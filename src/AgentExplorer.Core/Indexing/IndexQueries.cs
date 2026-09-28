using System.Text;

using AgentExplorer.Core.Models;
using AgentExplorer.Core.Querying;

using Microsoft.Data.Sqlite;

namespace AgentExplorer.Core.Indexing;

internal static class IndexQueries
{
    internal static IndexPage<IndexedInput> Inputs(string path, IndexInputQuery query, int pageSize,
        string? token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var store = new IndexStore(path, writable: false);
        store.EnsureCurrentNormalization();
        var revision = store.GetRevision();
        var queryHash = IndexCursor.QueryHashFor("inputs", query);
        var cursor = IndexCursor.Read(token, revision, queryHash, pageSize);
        using var command = store.Command("", ("$after", cursor?.InputId ?? ""), ("$limit", pageSize + 1));
        var sql = new StringBuilder("SELECT i.descriptor FROM inputs i WHERE i.id > $after");
        FilterInputs(command, sql, query);
        command.CommandText = sql.Append(" ORDER BY i.id LIMIT $limit;").ToString();
        using var reader = command.ExecuteReader();
        var items = new List<IndexedInput>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (items.Count == pageSize)
            {
                return new(items, revision, new IndexCursor(revision, queryHash, items[^1].Id, -1).Encode());
            }
            items.Add(IndexStore.Deserialize<IndexedInput>(reader.GetString(0)));
        }
        return new(items, revision, null);
    }

    internal static IndexPage<IndexedEvent> Events(string path, EventQuery query, IndexInputQuery inputs,
        int pageSize, string? token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var store = new IndexStore(path, writable: false);
        store.EnsureCurrentNormalization();
        var revision = store.GetRevision();
        var queryHash = IndexCursor.QueryHashFor("events", inputs, query);
        var cursor = IndexCursor.Read(token, revision, queryHash, pageSize);
        using var command = store.Command("", ("$after", cursor?.InputId ?? ""), ("$sequence", cursor?.Sequence ?? -1));
        var sql = new StringBuilder("""
            SELECT i.id, i.revision, i.session_id, e.data, r.text
            FROM events e JOIN inputs i ON i.id = e.input_id
            JOIN native_records r ON r.input_id = e.input_id AND r.record_index = e.record_index
            WHERE (e.input_id, e.sequence) > ($after, $sequence)
            """);
        FilterInputs(command, sql, inputs);
        FilterEvents(command, sql, query);
        command.CommandText = sql.Append(" ORDER BY e.input_id, e.sequence;").ToString();
        using var reader = command.ExecuteReader();
        var items = new List<IndexedEvent>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = IndexStore.Deserialize<TranscriptEvent>(reader.GetString(3));
            item = item with { Native = item.Native with { Text = reader.GetString(4) } };
            // Keep Unicode and foreign-path comparisons identical to streaming and in-memory queries.
            if (!query.Matches(item))
            {
                continue;
            }
            if (items.Count == pageSize)
            {
                var last = items[^1];
                return new(items, revision, new IndexCursor(revision, queryHash, last.InputId, last.Event.Sequence).Encode());
            }
            items.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), item));
        }
        return new(items, revision, null);
    }

    private static void FilterInputs(SqliteCommand command, StringBuilder sql, IndexInputQuery query)
    {
        Equal(command, sql, "i.id", "$input", query.InputId);
        Equal(command, sql, "i.source", "$source", query.Source is { } source ? (int)source : null);
        Equal(command, sql, "i.session_id", "$session", query.SessionId);
        Equal(command, sql, "i.workspace", "$workspace", query.Workspace);
    }

    private static void FilterEvents(SqliteCommand command, StringBuilder sql, EventQuery query)
    {
        Equal(command, sql, "e.role", "$role", query.Role);
        Equal(command, sql, "e.tool_name", "$tool", query.ToolName);
        Equal(command, sql, "e.model", "$model", query.Model);
        Equal(command, sql, "e.provider", "$provider", query.Provider);
        Equal(command, sql, "e.is_error", "$error", query.IsError);
        if (!query.IncludeDuplicates)
        {
            sql.Append(" AND e.is_duplicate = 0");
        }
        if (!query.IncludeMirrors)
        {
            sql.Append(" AND e.is_mirror = 0");
        }
        if (query.From is { } from)
        {
            sql.Append(" AND e.timestamp >= $from");
            command.Parameters.AddWithValue("$from", from.UtcTicks);
        }
        if (query.Until is { } until)
        {
            sql.Append(" AND e.timestamp < $until");
            command.Parameters.AddWithValue("$until", until.UtcTicks);
        }
        if (query.Kinds is { Count: 0 })
        {
            sql.Append(" AND 0");
        }
        else if (query.Kinds is not null)
        {
            var names = new List<string>();
            foreach (var kind in query.Kinds)
            {
                var name = "$kind" + names.Count;
                names.Add(name);
                command.Parameters.AddWithValue(name, (int)kind);
            }
            sql.Append(" AND e.kind IN (").AppendJoin(',', names).Append(')');
        }
    }

    private static void Equal(SqliteCommand command, StringBuilder sql, string column, string parameter, object? value)
    {
        if (value is null)
        {
            return;
        }
        sql.Append(" AND ").Append(column).Append(" = ").Append(parameter);
        command.Parameters.AddWithValue(parameter, value);
    }
}
