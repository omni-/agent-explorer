using System.IO.Compression;
using System.Text.Json;

using AgentExplorer.Core.Discovery;
using AgentExplorer.Core.Indexing;
using AgentExplorer.Core.Models;
using AgentExplorer.Core.Querying;

using Microsoft.Data.Sqlite;

using Xunit;

namespace AgentExplorer.Core.Tests;

public sealed class IndexTests
{
    [Theory]
    [InlineData("codex.jsonl")]
    [InlineData("claude.jsonl")]
    [InlineData("dsh.jsonl")]
    [InlineData("opencode-v1.json")]
    [InlineData("opencode-v2.json")]
    [InlineData("opencode-v2.md")]
    [InlineData("session.v3.jsonl.zstd")]
    [InlineData("dsh-bundle.zip")]
    public async Task ReopenedIndexPreservesEventsNativeLocationsAndStatistics(string fixture)
    {
        // Arrange
        using var directory = new TestDirectory();
        var database = Path.Combine(directory.Path, "index.db");
        var input = new TranscriptInput(FormatTests.Fixture(fixture));
        var bytesBefore = await File.ReadAllBytesAsync(input.Path);
        var session = await new TranscriptReader().LoadAsync(input, new ReadOptions { OrderByTimestamp = false });

        // Act
        var result = await new LocalTranscriptIndex(database).UpdateAsync([input]);
        var reopened = new LocalTranscriptIndex(database);
        var descriptor = Assert.Single(reopened.QueryInputs().Items);
        var events = AllEvents(reopened, new EventQuery { IncludeDuplicates = true }, pageSize: 2);

        // Assert
        Assert.Equal(1, result.UpdatedInputs);
        Assert.Equal(0, result.UnchangedInputs);
        Assert.Equal(session.Metadata, descriptor.Metadata);
        Assert.Equal(JsonSerializer.Serialize(session.Statistics), JsonSerializer.Serialize(descriptor.Statistics));
        Assert.Equal(session.Events.Select(item => JsonSerializer.Serialize(item)), events.Select(item => JsonSerializer.Serialize(item.Event)));
        Assert.All(events, item =>
        {
            Assert.Equal(descriptor.Id, item.InputId);
            Assert.Equal(descriptor.Revision, item.Revision);
            Assert.Equal(session.Metadata.SessionId, item.SessionId);
        });
        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(input.Path));
        using var connection = OpenDatabase(database);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM native_records;";
        Assert.Equal(session.NativeRecords.Count, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public async Task UnchangedInputsReuseRevisionsAndCursorsAcrossInstances()
    {
        // Arrange
        using var directory = new TestDirectory();
        var index = new LocalTranscriptIndex(Path.Combine(directory.Path, "index.db"));
        var input = new TranscriptInput(FormatTests.Fixture("codex.jsonl"));
        var initial = await index.UpdateAsync([input]);
        var first = index.QueryEvents(pageSize: 1);
        var descriptor = Assert.Single(index.QueryInputs().Items);

        // Act
        var reopened = new LocalTranscriptIndex(index.DatabasePath);
        var scan = await reopened.UpdateAsync([input, input]);
        var next = reopened.QueryEvents(pageSize: 2, continuationToken: first.ContinuationToken);

        // Assert
        Assert.Equal(0, scan.UpdatedInputs);
        Assert.Equal(1, scan.UnchangedInputs);
        Assert.Equal(initial.IndexRevision, scan.IndexRevision);
        Assert.Equal(descriptor.Revision, Assert.Single(reopened.QueryInputs().Items).Revision);
        Assert.DoesNotContain(next.Items, item => item.Event.Id == first.Items[0].Event.Id);
    }

    [Fact]
    public async Task AppendsRewritesTruncationAndSameLengthEditsReplaceTheWholeRevision()
    {
        // Arrange
        using var directory = new TestDirectory();
        var path = directory.Write("source.jsonl", Header("session") + Message("old", "one"));
        var index = new LocalTranscriptIndex(Path.Combine(directory.Path, "index.db"));
        await index.UpdateAsync([new(path)]);
        var original = Assert.Single(index.QueryInputs().Items);
        var oldPage = index.QueryEvents(pageSize: 1);

        // Act
        await File.AppendAllTextAsync(path, Message("new", "two"));
        var appended = await index.UpdateAsync([new(path)]);
        var afterAppend = Assert.Single(index.QueryInputs().Items);
        var timestamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, Header("session") + Message("old", "ONE") + Message("new", "TWO"));
        File.SetLastWriteTimeUtc(path, timestamp);
        var rewritten = await index.UpdateAsync([new(path)]);
        var afterRewrite = Assert.Single(index.QueryInputs().Items);
        var rewrittenEvents = AllEvents(index);
        await File.WriteAllTextAsync(path, Header("session"));
        var truncated = await index.UpdateAsync([new(path)]);

        // Assert
        Assert.Equal(1, appended.UpdatedInputs);
        Assert.Equal(original.Id, afterAppend.Id);
        Assert.NotEqual(original.Revision, afterAppend.Revision);
        Assert.Equal(afterAppend.ByteLength, afterRewrite.ByteLength);
        Assert.Equal(1, rewritten.UpdatedInputs);
        Assert.NotEqual(afterAppend.Revision, afterRewrite.Revision);
        Assert.Contains(rewrittenEvents, item => item.Event.Text == "ONE");
        Assert.DoesNotContain(rewrittenEvents, item => item.Event.Text == "one");
        Assert.Equal(1, truncated.UpdatedInputs);
        Assert.Single(AllEvents(index));
        Assert.Throws<StaleIndexCursorException>(() => index.QueryEvents(pageSize: 1, continuationToken: oldPage.ContinuationToken));
    }

    [Fact]
    public async Task PartialTailCompletionRebuildsContextAndRemovesOldDiagnostics()
    {
        // Arrange
        using var directory = new TestDirectory();
        var path = directory.Write("partial.jsonl", Header("partial") + "{\"type\":\"turn_context\",\"payload\":{\"model\":\"active\"}}\n{broken");
        var index = new LocalTranscriptIndex(Path.Combine(directory.Path, "index.db"));
        var partial = await index.UpdateAsync([new(path)]);
        var diagnostics = index.QueryEvents(new EventQuery { Kinds = new HashSet<EventKind> { EventKind.Diagnostic } });

        // Act
        await File.WriteAllTextAsync(path, Header("partial") + "{\"type\":\"turn_context\",\"payload\":{\"model\":\"active\"}}\n" + Message("one", "done"));
        var completed = await index.UpdateAsync([new(path)]);

        // Assert
        Assert.NotEmpty(partial.Issues);
        Assert.Single(diagnostics.Items);
        Assert.Empty(completed.Issues);
        Assert.Empty(index.QueryEvents(new EventQuery { Kinds = new HashSet<EventKind> { EventKind.Diagnostic } }).Items);
        Assert.Equal("active", Assert.Single(index.QueryEvents(new EventQuery { Kinds = new HashSet<EventKind> { EventKind.Message } }).Items).Event.Model);
    }

    [Fact]
    public async Task CrossSessionQueriesKeepSameIdExportsAndAnonymousInputsSeparate()
    {
        // Arrange
        using var directory = new TestDirectory();
        var one = directory.Write("one.jsonl", Header("same", "workspace") + Message("1", "HELLO 世界"));
        var two = directory.Write("two.jsonl", Header("same", "workspace") + Message("2", "hello 世界"));
        var other = directory.Write("other.jsonl", Header("other", "elsewhere") + Message("3", "hello 世界"));
        var anonymous = directory.Write("anonymous.jsonl", Message("4", "hello 世界"));
        var anotherAnonymous = directory.Write("another-anonymous.jsonl", Message("5", "hello 世界"));
        var index = new LocalTranscriptIndex(Path.Combine(directory.Path, "index.db"));
        await index.UpdateAsync([new(one), new(two), new(other), new(anonymous), new(anotherAnonymous)]);

        // Act
        foreach (var path in new[] { one, two, other, anonymous, anotherAnonymous })
        {
            File.Delete(path);
        }
        var query = new EventQuery { Text = "hello 世界", Kinds = new HashSet<EventKind> { EventKind.Message } };
        var all = AllEvents(index, query, pageSize: 1);
        var same = AllEvents(index, query, new IndexInputQuery { Source = AgentSource.Codex, SessionId = "same", Workspace = "workspace" }, 1);

        // Assert
        Assert.Equal(5, index.QueryInputs().Items.Count);
        Assert.Equal(5, all.Count);
        Assert.Equal(5, all.Select(item => item.InputId).Distinct().Count());
        Assert.Equal(2, same.Count);
        Assert.All(same, item => Assert.Equal("same", item.SessionId));
        Assert.Equal(2, all.Count(item => item.SessionId is null));
        Assert.Empty(index.QueryInputs(new IndexInputQuery { SessionId = "SAME" }).Items);
        Assert.Empty(index.QueryEvents(query, new IndexInputQuery { Workspace = "Workspace" }).Items);
    }

    [Fact]
    public async Task AllEventFiltersMatchTheReaderIncludingNativeSearchAndUtcTimeBounds()
    {
        // Arrange
        using var directory = new TestDirectory();
        var inputs = new[] { "codex.jsonl", "claude.jsonl", "dsh.jsonl", "opencode-v2.json" }.Select(file => new TranscriptInput(FormatTests.Fixture(file))).ToArray();
        var expected = new List<TranscriptEvent>();
        foreach (var input in inputs)
        {
            expected.AddRange((await new TranscriptReader().LoadAsync(input)).Events);
        }
        var index = new LocalTranscriptIndex(Path.Combine(directory.Path, "index.db"));
        await index.UpdateAsync(inputs);
        var timestamp = expected.First(item => item.Timestamp is not null).Timestamp!.Value;
        var queries = new EventQuery[]
        {
            new(), new() { IncludeDuplicates = true }, new() { IncludeMirrors = false },
            new() { Kinds = new HashSet<EventKind>() }, new() { Kinds = new HashSet<EventKind> { EventKind.ToolCall, EventKind.ToolResult } },
            new() { Role = "assistant" }, new() { ToolName = "exec_command" }, new() { Model = "test-model" },
            new() { Provider = "test-provider" }, new() { IsError = true }, new() { IsError = false },
            new() { Text = "opaque-test-payload", SearchNativeData = true }, new() { Text = "%_'" },
            new() { FilePath = "C:\\sample\\a.cs" }, new() { FilePath = "c:\\sample\\a.cs" },
            new() { From = timestamp.ToOffset(TimeSpan.FromHours(5)), Until = timestamp.AddSeconds(5).ToOffset(TimeSpan.FromHours(-3)) }
        };

        // Act / Assert
        foreach (var query in queries)
        {
            var actual = AllEvents(index, query, pageSize: 3);
            Assert.Equal(expected.Where(query.Matches).Select(item => JsonSerializer.Serialize(item)).Order(), actual.Select(item => JsonSerializer.Serialize(item.Event)).Order());
        }
    }

    [Fact]
    public async Task InputPaginationAndTokensAreBoundToTheIndexAndQuery()
    {
        // Arrange
        using var directory = new TestDirectory();
        var paths = Enumerable.Range(0, 5).Select(i => new TranscriptInput(directory.Write($"{i}.jsonl", Header(i.ToString())))).ToArray();
        var index = new LocalTranscriptIndex(Path.Combine(directory.Path, "index.db"));
        await index.UpdateAsync(paths);
        var first = index.QueryInputs(pageSize: 2);

        // Act
        var second = index.QueryInputs(pageSize: 2, continuationToken: first.ContinuationToken);
        var last = index.QueryInputs(pageSize: 2, continuationToken: second.ContinuationToken);
        var all = first.Items.Concat(second.Items).Concat(last.Items).ToArray();

        // Assert
        Assert.Equal(5, all.Length);
        Assert.Equal(5, all.Select(input => input.Id).Distinct().Count());
        Assert.Equal(all.Select(input => input.Id).Order(StringComparer.Ordinal), all.Select(input => input.Id));
        Assert.Null(last.ContinuationToken);
        Assert.Throws<ArgumentException>(() => index.QueryInputs(new IndexInputQuery { SessionId = "0" }, continuationToken: first.ContinuationToken));
        Assert.Throws<ArgumentException>(() => index.QueryEvents(continuationToken: first.ContinuationToken));
        Assert.Throws<ArgumentException>(() => index.QueryInputs(continuationToken: "garbage"));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.QueryInputs(pageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.QueryEvents(pageSize: 1001));
        var otherIndex = new LocalTranscriptIndex(Path.Combine(directory.Path, "other.db"));
        await otherIndex.UpdateAsync(paths);
        Assert.Throws<StaleIndexCursorException>(() => otherIndex.QueryInputs(continuationToken: first.ContinuationToken));
    }

    [Fact]
    public async Task FailedInputsKeepOldRevisionsAndExplicitPruningRemovesOnlyAbsentInputs()
    {
        // Arrange
        using var directory = new TestDirectory();
        var one = directory.Write("one.jsonl", Header("one"));
        var two = directory.Write("two.jsonl", Header("two"));
        var index = new LocalTranscriptIndex(Path.Combine(directory.Path, "index.db"));
        await index.UpdateAsync([new(one), new(two)]);
        var original = index.QueryInputs(new IndexInputQuery { SessionId = "one" }).Items[0];

        // Act
        await File.WriteAllTextAsync(one, "unrecognized replacement");
        var failed = await index.UpdateAsync([new(one)], removeAbsentInputs: true);
        var afterFailure = Assert.Single(index.QueryInputs().Items);
        var deleted = await index.UpdateAsync([], removeAbsentInputs: true);

        // Assert
        Assert.Single(failed.Issues);
        Assert.Equal(0, failed.UpdatedInputs);
        Assert.Equal(1, failed.RemovedInputs);
        Assert.Equal(original.Revision, afterFailure.Revision);
        Assert.Equal(1, deleted.RemovedInputs);
        Assert.Empty(index.QueryInputs().Items);
        Assert.Empty(index.QueryEvents().Items);
    }

    [Fact]
    public async Task DiscoveryRescansAllAlternativesAndPrunesDeletedSources()
    {
        // Arrange
        using var directory = new TestDirectory();
        var one = directory.Write("exports/one.jsonl", Header("same") + Message("1", "one"));
        directory.Write("exports/two.jsonl", Header("same") + Message("2", "two"));
        var options = new DiscoveryOptions { UseDefaultLocations = false, ExportDirectories = [Path.GetDirectoryName(one)!] };
        var index = new LocalTranscriptIndex(Path.Combine(directory.Path, "index.db"));
        var first = await index.RescanAsync(options);

        // Act
        File.Delete(one);
        var next = await index.RescanAsync(options);

        // Assert
        Assert.Equal(2, first.UpdatedInputs);
        Assert.Equal(1, next.UnchangedInputs);
        Assert.Equal(1, next.RemovedInputs);
        Assert.Single(index.QueryInputs().Items);
        Assert.DoesNotContain(AllEvents(index), item => item.Event.Text == "one");
    }

    [Fact]
    public async Task ArchiveMembersHaveSeparateIdentityAndRootShorthandDoesNotDuplicateIt()
    {
        // Arrange
        using var directory = new TestDirectory();
        var zipPath = Path.Combine(directory.Path, "bundle.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var entry in new[] { "session.v3.jsonl", "subagents/child/session.v3.jsonl" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                await writer.WriteAsync("{\"type\":\"session\",\"version\":3,\"id\":\"same\"}\n");
            }
        }
        var index = new LocalTranscriptIndex(Path.Combine(directory.Path, "index.db"));

        // Act
        var scan = await index.UpdateAsync([new(zipPath), new(zipPath, ArchiveEntry: "session.v3.jsonl"), new(zipPath, ArchiveEntry: "subagents/child/session.v3.jsonl")]);
        var events = AllEvents(index);

        // Assert
        Assert.Equal(2, scan.UpdatedInputs);
        Assert.Equal(2, events.Select(item => item.InputId).Distinct().Count());
        Assert.Equal(2, events.Select(item => item.Event.Native.Location.ArchiveEntry).Distinct().Count());
        Assert.All(events, item => Assert.Equal(zipPath, item.Event.Native.Location.Path));
    }

    [Fact]
    public async Task ReadLimitChangesInvalidateCachedEventsAndStreamingIgnoresLoadedEventLimit()
    {
        // Arrange
        using var directory = new TestDirectory();
        var database = Path.Combine(directory.Path, "index.db");
        var input = new TranscriptInput(FormatTests.Fixture("codex.jsonl"));
        var limited = new LocalTranscriptIndex(database, new ReadOptions { MaxRecordCharacters = 50 });
        await limited.UpdateAsync([input]);
        var old = Assert.Single(limited.QueryInputs().Items);

        // Act
        var unlimited = new LocalTranscriptIndex(database, new ReadOptions { MaxLoadedEvents = 1 });
        var scan = await unlimited.UpdateAsync([input]);

        // Assert
        Assert.Equal(1, scan.UpdatedInputs);
        Assert.NotEqual(old.Revision, Assert.Single(unlimited.QueryInputs().Items).Revision);
        Assert.True(AllEvents(unlimited).Count > 1);
        Assert.Empty(unlimited.QueryEvents(new EventQuery { Kinds = new HashSet<EventKind> { EventKind.Diagnostic } }).Items);
    }

    [Fact]
    public async Task CancellationRollsBackTheScanAndLeavesThePreviousPageUsable()
    {
        // Arrange
        using var directory = new TestDirectory();
        var path = directory.Write("large.jsonl", Header("large") + Message("one", "old"));
        var index = new LocalTranscriptIndex(Path.Combine(directory.Path, "index.db"));
        await index.UpdateAsync([new(path)]);
        var before = index.QueryEvents(pageSize: 1);
        await File.AppendAllTextAsync(path, string.Concat(Enumerable.Range(0, 20000).Select(i => Message(i.ToString(), "new"))));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.UpdateAsync([new(path)], cancellationToken: cancellation.Token));
        Assert.Equal(before.IndexRevision, index.QueryInputs().IndexRevision);
        Assert.Equal("old", Assert.Single(index.QueryEvents(pageSize: 1, continuationToken: before.ContinuationToken).Items).Event.Text);
        Assert.Equal(2, AllEvents(index).Count);
    }

    [Fact]
    public async Task IncompatibleSchemasAreRejectedWithoutChangingTheDatabase()
    {
        // Arrange
        using var directory = new TestDirectory();
        var database = Path.Combine(directory.Path, "index.db");
        var index = new LocalTranscriptIndex(database);
        await index.UpdateAsync([]);
        using (var connection = OpenDatabase(database))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 999;";
            command.ExecuteNonQuery();
        }
        var before = await File.ReadAllBytesAsync(database);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => index.UpdateAsync([]));
        Assert.Throws<InvalidDataException>(() => index.QueryInputs());
        Assert.Equal(before, await File.ReadAllBytesAsync(database));
    }

    [Fact]
    public async Task OldNormalizationRequiresRescanAndRebuildsOnlyAffectedInputs()
    {
        // Arrange
        using var directory = new TestDirectory();
        var index = new LocalTranscriptIndex(Path.Combine(directory.Path, "index.db"));
        var paths = new[] { "codex.jsonl", "claude.jsonl" }.Select(file => new TranscriptInput(FormatTests.Fixture(file))).ToArray();
        await index.UpdateAsync(paths);
        var original = index.QueryInputs();
        using (var connection = OpenDatabase(index.DatabasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE inputs SET normalization_version = 0, descriptor = $descriptor WHERE id = $id;";
            command.Parameters.AddWithValue("$descriptor", JsonSerializer.Serialize(original.Items[0] with { NormalizationVersion = 0 }));
            command.Parameters.AddWithValue("$id", original.Items[0].Id);
            command.ExecuteNonQuery();
        }

        // Act / Assert
        Assert.Throws<InvalidDataException>(() => index.QueryEvents());
        var scan = await index.UpdateAsync(paths);
        Assert.Equal(1, scan.UpdatedInputs);
        Assert.Equal(1, scan.UnchangedInputs);
        Assert.NotEqual(original.IndexRevision, scan.IndexRevision);
        Assert.All(index.QueryInputs().Items, input => Assert.Equal(TranscriptReader.NormalizationVersion, input.NormalizationVersion));
    }

    [Fact]
    public async Task DiscoveryFailureDoesNotPruneThePreviousCatalog()
    {
        // Arrange
        using var directory = new TestDirectory();
        var removed = directory.Write("exports/removed.jsonl", Header("removed"));
        var broken = directory.Write("exports/broken.jsonl", Header("broken"));
        var options = new DiscoveryOptions { UseDefaultLocations = false, ExportDirectories = [Path.GetDirectoryName(removed)!] };
        var index = new LocalTranscriptIndex(Path.Combine(directory.Path, "index.db"));
        await index.RescanAsync(options);

        // Act
        File.Delete(removed);
        await File.WriteAllTextAsync(broken, "unrecognized input");
        var scan = await index.RescanAsync(options);

        // Assert
        Assert.NotEmpty(scan.Issues);
        Assert.Equal(0, scan.RemovedInputs);
        Assert.Equal(2, index.QueryInputs().Items.Count);
    }

    [Fact]
    public async Task ReorderingKindFiltersPreservesTheCursorButChangingFiltersRejectsIt()
    {
        // Arrange
        using var directory = new TestDirectory();
        var index = new LocalTranscriptIndex(Path.Combine(directory.Path, "index.db"));
        await index.UpdateAsync([new(FormatTests.Fixture("codex.jsonl"))]);
        var query = new EventQuery { Kinds = new HashSet<EventKind> { EventKind.ToolCall, EventKind.Message } };
        var page = index.QueryEvents(query, pageSize: 1);

        // Act
        var next = index.QueryEvents(query with { Kinds = new HashSet<EventKind> { EventKind.Message, EventKind.ToolCall } },
            pageSize: 1, continuationToken: page.ContinuationToken);

        // Assert
        Assert.Single(next.Items);
        Assert.NotEqual(page.Items[0].Event.Id, next.Items[0].Event.Id);
        Assert.Throws<ArgumentException>(() => index.QueryEvents(query with { Text = "different" }, continuationToken: page.ContinuationToken));
    }

    [Fact]
    public async Task ConflictingExplicitFormatsDoNotSilentlyReuseAnotherInterpretation()
    {
        // Arrange
        using var directory = new TestDirectory();
        var index = new LocalTranscriptIndex(Path.Combine(directory.Path, "index.db"));
        var input = new TranscriptInput(FormatTests.Fixture("codex.jsonl"));
        var original = await index.UpdateAsync([input]);

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentException>(() => index.UpdateAsync([input, input with { Format = TranscriptFormat.ClaudeCodeJsonl }]));
        Assert.Equal(original.IndexRevision, index.QueryInputs().IndexRevision);
        Assert.Equal(TranscriptFormat.CodexJsonl, Assert.Single(index.QueryInputs().Items).Input.Format);
    }

    private static List<IndexedEvent> AllEvents(LocalTranscriptIndex index, EventQuery? query = null,
        IndexInputQuery? inputs = null, int pageSize = 100)
    {
        var result = new List<IndexedEvent>();
        string? token = null;
        do
        {
            var page = index.QueryEvents(query, inputs, pageSize, token);
            result.AddRange(page.Items);
            token = page.ContinuationToken;
        } while (token is not null);
        return result;
    }

    private static string Header(string id, string workspace = "test") =>
        JsonSerializer.Serialize(new { type = "session_meta", payload = new { id, cwd = workspace } }) + "\n";

    private static string Message(string id, string text) => JsonSerializer.Serialize(new
    {
        type = "response_item",
        payload = new { type = "message", id, role = "user", content = new[] { new { type = "input_text", text } } }
    }) + "\n";

    private static SqliteConnection OpenDatabase(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
