using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace AgentExplorer.Core.Indexing;

internal sealed class IndexStore : IDisposable
{
    private const int ApplicationId = 0x41455850;
    private const int SchemaVersion = 1;

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    internal IndexStore(string path, bool writable)
    {
        if (writable)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = writable ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            _connection.Open();
            if (writable)
            {
                Initialize();
            }

            _transaction = _connection.BeginTransaction(deferred: !writable);
            using var version = Command("PRAGMA user_version;");
            using var application = Command("PRAGMA application_id;");
            if (Convert.ToInt32(version.ExecuteScalar()) != SchemaVersion || Convert.ToInt32(application.ExecuteScalar()) != ApplicationId)
            {
                throw new InvalidDataException("Unsupported index schema. Use a new index path and rescan the sources.");
            }
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    internal SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command;
    }

    internal string GetRevision()
    {
        using var command = Command("SELECT revision FROM index_state WHERE id = 1;");
        return (string?)command.ExecuteScalar() ?? throw new InvalidDataException("Missing index state.");
    }

    internal string AdvanceRevision()
    {
        var revision = Guid.NewGuid().ToString("N");
        using var command = Command("UPDATE index_state SET revision = $revision WHERE id = 1;", ("$revision", revision));
        command.ExecuteNonQuery();
        return revision;
    }

    internal List<IndexedInput> ReadInputs()
    {
        using var command = Command("SELECT descriptor FROM inputs ORDER BY id;");
        using var reader = command.ExecuteReader();
        var inputs = new List<IndexedInput>();
        while (reader.Read())
        {
            inputs.Add(Deserialize<IndexedInput>(reader.GetString(0)));
        }
        return inputs;
    }

    internal void DeleteInput(string id)
    {
        using var command = Command("DELETE FROM inputs WHERE id = $id;", ("$id", id));
        command.ExecuteNonQuery();
    }

    internal void SaveInput(IndexedInput input)
    {
        using var command = Command("""
            INSERT INTO inputs (id, source, session_id, workspace, revision, normalization_version, descriptor)
            VALUES ($id, $source, $session, $workspace, $revision, $normalization, $descriptor);
            """, ("$id", input.Id), ("$source", (int)input.Source), ("$session", input.Metadata.SessionId),
            ("$workspace", input.Metadata.Workspace), ("$revision", input.Revision),
            ("$normalization", input.NormalizationVersion), ("$descriptor", JsonSerializer.Serialize(input)));
        command.ExecuteNonQuery();
    }

    internal void EnsureCurrentNormalization()
    {
        using var command = Command("SELECT 1 FROM inputs WHERE normalization_version <> $version LIMIT 1;",
            ("$version", TranscriptReader.NormalizationVersion));
        if (command.ExecuteScalar() is not null)
        {
            throw new InvalidDataException("The index contains an older normalization version. Rescan its inputs before querying.");
        }
    }

    internal void Savepoint() => _transaction.Save("input");

    internal void Release() => _transaction.Release("input");

    internal void RollbackInput()
    {
        _transaction.Rollback("input");
        _transaction.Release("input");
    }

    internal void Commit() => _transaction.Commit();

    internal static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json)
        ?? throw new InvalidDataException("Invalid data in local index.");

    public void Dispose()
    {
        _transaction.Dispose();
        _connection.Dispose();
    }

    private void Initialize()
    {
        using (var transaction = _connection.BeginTransaction())
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "PRAGMA application_id;";
            var application = Convert.ToInt32(command.ExecuteScalar());
            command.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(command.ExecuteScalar());
            if (application == ApplicationId && version == SchemaVersion)
            {
                transaction.Commit();
                return;
            }

            command.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
            if (application != 0 || version != 0 || Convert.ToInt64(command.ExecuteScalar()) != 0)
            {
                throw new InvalidDataException("Unsupported index schema. Use a new index path and rescan the sources.");
            }

            command.CommandText = $"""
                PRAGMA application_id = {ApplicationId};
                PRAGMA user_version = {SchemaVersion};
                CREATE TABLE index_state (id INTEGER PRIMARY KEY CHECK (id = 1), revision TEXT NOT NULL);
                INSERT INTO index_state VALUES (1, $revision);
                CREATE TABLE inputs (
                    id TEXT PRIMARY KEY, source INTEGER NOT NULL, session_id TEXT, workspace TEXT,
                    revision TEXT NOT NULL, normalization_version INTEGER NOT NULL, descriptor TEXT NOT NULL
                );
                CREATE INDEX inputs_session ON inputs(source, session_id, id);
                CREATE INDEX inputs_workspace ON inputs(workspace, id);
                CREATE TABLE native_records (
                    input_id TEXT NOT NULL REFERENCES inputs(id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
                    record_index INTEGER NOT NULL, text TEXT NOT NULL,
                    PRIMARY KEY (input_id, record_index)
                );
                CREATE TABLE events (
                    input_id TEXT NOT NULL, sequence INTEGER NOT NULL, record_index INTEGER NOT NULL,
                    kind INTEGER NOT NULL, role TEXT, tool_name TEXT, model TEXT, provider TEXT,
                    timestamp INTEGER, is_error INTEGER NOT NULL, is_mirror INTEGER NOT NULL,
                    is_duplicate INTEGER NOT NULL, data TEXT NOT NULL,
                    PRIMARY KEY (input_id, sequence),
                    FOREIGN KEY (input_id, record_index) REFERENCES native_records(input_id, record_index) ON DELETE CASCADE
                );
                CREATE INDEX events_kind ON events(kind, input_id, sequence);
                CREATE INDEX events_time ON events(timestamp, input_id, sequence);
                """;
            command.Parameters.AddWithValue("$revision", Guid.NewGuid().ToString("N"));
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        using var journal = _connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode = WAL;";
        journal.ExecuteScalar();
    }
}
