using System.Text.Json;

using AgentExplorer.Core.Analysis;
using AgentExplorer.Core.Discovery;
using AgentExplorer.Core.IO;
using AgentExplorer.Core.Models;
using AgentExplorer.Core.Parsing;
using AgentExplorer.Core.Querying;

using Microsoft.Data.Sqlite;

namespace AgentExplorer.Core.Indexing;

/// <summary>A local SQLite index. Sources are only read; index writes are transactional.</summary>
public sealed class LocalTranscriptIndex
{
    public string DatabasePath { get; }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ReadOptions _readOptions;

    public LocalTranscriptIndex(string databasePath, ReadOptions? readOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _readOptions = readOptions ?? new ReadOptions();
        if (_readOptions.MaxRecordCharacters <= 0 || _readOptions.MaxDecodedCharacters <= 0 || _readOptions.MaxLoadedEvents <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(readOptions));
        }
    }

    /// <summary>Discovers the complete catalog, updates changed inputs, and removes absent inputs when discovery succeeds.</summary>
    public async Task<IndexScanResult> RescanAsync(DiscoveryOptions? options = null, CancellationToken cancellationToken = default)
    {
        var discovery = await new SessionDiscovery().DiscoverAsync(options, cancellationToken).ConfigureAwait(false);
        var inputs = discovery.Sessions.SelectMany(session => session.AlternativeInputs.Prepend(session.PreferredInput));
        return await ScanAsync(inputs, discovery.Issues.Count == 0, [.. discovery.Issues], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Updates explicit inputs. Set removeAbsentInputs only when inputs is the complete desired catalog.</summary>
    public Task<IndexScanResult> UpdateAsync(IEnumerable<TranscriptInput> inputs, bool removeAbsentInputs = false,
        CancellationToken cancellationToken = default) => ScanAsync(inputs, removeAbsentInputs, [], cancellationToken);

    /// <summary>Pages inputs by stable input ID, including separate exports with the same native session ID.</summary>
    public IndexPage<IndexedInput> QueryInputs(IndexInputQuery? query = null, int pageSize = 100,
        string? continuationToken = null, CancellationToken cancellationToken = default) =>
        IndexQueries.Inputs(DatabasePath, query ?? new IndexInputQuery(), pageSize, continuationToken, cancellationToken);

    /// <summary>Pages matching events by input ID and native sequence, using the existing EventQuery semantics.</summary>
    public IndexPage<IndexedEvent> QueryEvents(EventQuery? query = null, IndexInputQuery? inputs = null,
        int pageSize = 100, string? continuationToken = null, CancellationToken cancellationToken = default) =>
        IndexQueries.Events(DatabasePath, query ?? new EventQuery(), inputs ?? new IndexInputQuery(), pageSize, continuationToken, cancellationToken);

    private async Task<IndexScanResult> ScanAsync(IEnumerable<TranscriptInput> inputs, bool removeAbsentInputs,
        List<DiscoveryIssue> issues, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        cancellationToken.ThrowIfCancellationRequested();
        var groups = inputs.Select(input => input with { Path = Path.GetFullPath(input.Path) })
            .GroupBy(input => input.Path, PathComparer).ToArray();
        if (groups.Any(group => PathComparer.Equals(group.Key, DatabasePath)))
        {
            throw new ArgumentException("The index database cannot also be a transcript input.", nameof(inputs));
        }

        using var store = new IndexStore(DatabasePath, writable: true);
        var previous = store.ReadInputs().ToDictionary(input => input.Id, StringComparer.Ordinal);
        var seen = new Dictionary<string, TranscriptFormat?>();
        var failedPaths = new HashSet<string>(PathComparer);
        var updated = 0;
        var unchanged = 0;
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var snapshot = await InputSnapshot.OpenAsync(group.Key, cancellationToken).ConfigureAwait(false);
                foreach (var supplied in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var input = supplied;
                    try
                    {
                        // Resolve ZIP root shorthand to the same identity as an explicit member selection.
                        using (var source = new InputStream(input))
                        {
                            input = input with { ArchiveEntry = source.Entry };
                        }
                        var id = InputId(input);
                        if (seen.TryGetValue(id, out var suppliedFormat))
                        {
                            if (suppliedFormat is not null && input.Format is not null && suppliedFormat != input.Format)
                            {
                                throw new ArgumentException($"Conflicting formats for input: {input.Path}!{input.ArchiveEntry}", nameof(inputs));
                            }
                            continue;
                        }
                        seen.Add(id, input.Format);
                        previous.TryGetValue(id, out var old);
                        var knownFormat = input.Format ?? old?.Input.Format;
                        if (old is not null && knownFormat is { } format && old.NormalizationVersion == TranscriptReader.NormalizationVersion &&
                            old.Revision == Revision(snapshot.ContentSha256, format))
                        {
                            seen[id] = format;
                            unchanged++;
                            continue;
                        }

                        var copy = await snapshot.CopyAsync(cancellationToken).ConfigureAwait(false);
                        store.Savepoint();
                        try
                        {
                            store.DeleteInput(id);
                            var diagnostics = new List<DiscoveryIssue>();
                            var indexed = await IndexInputAsync(store, id, input, copy, snapshot, diagnostics, cancellationToken).ConfigureAwait(false);
                            store.SaveInput(indexed);
                            store.Release();
                            seen[id] = indexed.Input.Format;
                            issues.AddRange(diagnostics);
                            updated++;
                        }
                        catch
                        {
                            store.RollbackInput();
                            throw;
                        }
                    }
                    catch (Exception ex) when (IsInputFailure(ex))
                    {
                        failedPaths.Add(group.Key);
                        issues.Add(new(input.Path, $"{input.ArchiveEntry ?? "file"}: {ex.Message}"));
                    }
                }
            }
            catch (Exception ex) when (IsInputFailure(ex))
            {
                failedPaths.Add(group.Key);
                issues.Add(new(group.Key, ex.Message));
            }
        }

        var removed = 0;
        if (removeAbsentInputs)
        {
            foreach (var old in previous.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.ContainsKey(old.Id) && !failedPaths.Contains(old.Input.Path))
                {
                    store.DeleteInput(old.Id);
                    removed++;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var revision = updated > 0 || removed > 0 ? store.AdvanceRevision() : store.GetRevision();
        store.Commit();
        return new IndexScanResult(revision, updated, unchanged, removed, issues);
    }

    private async Task<IndexedInput> IndexInputAsync(IndexStore store, string id, TranscriptInput input,
        string copy, InputSnapshot snapshot, List<DiscoveryIssue> issues, CancellationToken cancellationToken)
    {
        var reader = new TranscriptReader();
        var temporaryInput = input with { Path = copy };
        var format = await reader.DetectFormatAsync(temporaryInput, cancellationToken).ConfigureAwait(false);
        var metadata = new SessionMetadata();
        var statistics = new SessionStatisticsBuilder();
        using var recordCommand = store.Command("INSERT INTO native_records VALUES ($input, $record, $text);", ("$input", id));
        recordCommand.Parameters.Add("$record", SqliteType.Integer);
        recordCommand.Parameters.Add("$text", SqliteType.Text);
        using var eventCommand = store.Command("""
            INSERT INTO events VALUES ($input, $sequence, $record, $kind, $role, $tool, $model, $provider,
                $timestamp, $error, $mirror, $duplicate, $data);
            """, ("$input", id));
        foreach (var parameter in new[] { "$sequence", "$record", "$kind", "$timestamp", "$error", "$mirror", "$duplicate" })
        {
            eventCommand.Parameters.Add(parameter, SqliteType.Integer);
        }
        foreach (var parameter in new[] { "$role", "$tool", "$model", "$provider", "$data" })
        {
            eventCommand.Parameters.Add(parameter, SqliteType.Text);
        }

        long lastRecord = -1;
        await foreach (var parsed in reader.ReadEventsAsync(temporaryInput with { Format = format }, _readOptions, cancellationToken).ConfigureAwait(false))
        {
            var item = parsed with { Native = parsed.Native with { Location = parsed.Native.Location with { Path = input.Path } } };
            statistics.Add(item);
            if (item.Diagnostic is { Severity: not "information" } diagnostic)
            {
                issues.Add(new(input.Path, $"{input.ArchiveEntry ?? "file"}, record {item.Native.Location.RecordIndex}: {diagnostic.Message}"));
            }
            if (item.DuplicateOf is null && item.Metadata is { } update)
            {
                metadata = MetadataMerge.Apply(metadata, update);
            }
            if (item.Native.Location.RecordIndex != lastRecord)
            {
                recordCommand.Parameters["$record"].Value = item.Native.Location.RecordIndex;
                recordCommand.Parameters["$text"].Value = item.Native.Text;
                recordCommand.ExecuteNonQuery();
                lastRecord = item.Native.Location.RecordIndex;
            }

            eventCommand.Parameters["$sequence"].Value = item.Sequence;
            eventCommand.Parameters["$record"].Value = lastRecord;
            eventCommand.Parameters["$kind"].Value = (int)item.Kind;
            eventCommand.Parameters["$role"].Value = (object?)item.Role ?? DBNull.Value;
            eventCommand.Parameters["$tool"].Value = (object?)item.Tool?.Name ?? DBNull.Value;
            eventCommand.Parameters["$model"].Value = (object?)item.Model ?? DBNull.Value;
            eventCommand.Parameters["$provider"].Value = (object?)item.Provider ?? DBNull.Value;
            eventCommand.Parameters["$timestamp"].Value = (object?)item.Timestamp?.UtcTicks ?? DBNull.Value;
            eventCommand.Parameters["$error"].Value = item.IsError;
            eventCommand.Parameters["$mirror"].Value = item.IsMirror;
            eventCommand.Parameters["$duplicate"].Value = item.DuplicateOf is not null;
            // Store native text once even when a record expands into many events.
            eventCommand.Parameters["$data"].Value = JsonSerializer.Serialize(item with { Native = item.Native with { Text = "" } });
            eventCommand.ExecuteNonQuery();
        }

        return new IndexedInput
        {
            Id = id,
            Input = input with { Format = format },
            Source = new ParsingContext(format).Source,
            Metadata = metadata,
            Revision = Revision(snapshot.ContentSha256, format),
            ContentSha256 = snapshot.ContentSha256,
            ByteLength = snapshot.ByteLength,
            NormalizationVersion = TranscriptReader.NormalizationVersion,
            Statistics = statistics.Build()
        };
    }

    private string Revision(string hash, TranscriptFormat format) => IndexCursor.Hash(new
    {
        hash,
        format,
        TranscriptReader.NormalizationVersion,
        _readOptions.MaxRecordCharacters,
        _readOptions.MaxDecodedCharacters
    });

    private static string InputId(TranscriptInput input) => IndexCursor.Hash(new
    {
        Path = OperatingSystem.IsWindows() ? input.Path.ToUpperInvariant() : input.Path,
        input.ArchiveEntry
    });

    private static bool IsInputFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException ||
        ex.GetType().Namespace == "ZstdSharp";
}
