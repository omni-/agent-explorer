using System.Text.Json;

using AgentExplorer.Core.Analysis;
using AgentExplorer.Core.Models;
using AgentExplorer.Core.Querying;

using Xunit;

namespace AgentExplorer.Core.Tests;

public sealed class RobustnessTests
{
    [Fact]
    public async Task MalformedLinesDoNotHideLaterRecordsAndPreserveInvalidText()
    {
        using var directory = new TestDirectory();
        var path = directory.Write("partial.jsonl", "{broken\r\n42\n{\"type\":\"future\",\"new\":[1,2]}\n{\"type\":");
        var session = await new TranscriptReader().LoadAsync(new(path, TranscriptFormat.CodexJsonl));

        Assert.Equal(4, session.Statistics.NativeRecords);
        Assert.Equal(3, session.Statistics.DiagnosticEvents);
        Assert.Contains(session.Events, x => x.Kind == EventKind.Unknown);
        Assert.Equal("{broken\r\n", session.Events[0].Native.Text);
        Assert.Null(session.Statistics.ObservedDuration);
    }

    [Fact]
    public async Task PartialJsonExportPreservesCompleteMessagesAndTruncatedTail()
    {
        using var directory = new TestDirectory();
        var path = directory.Write("partial.json", "{\"info\":{\"id\":\"partial\"},\"messages\":[{\"type\":\"user\",\"text\":\"hello\",\"id\":\"u1\"},{\"type\":");
        var session = await new TranscriptReader().LoadAsync(new(path));

        Assert.Equal("partial", session.Metadata.SessionId);
        Assert.Contains(session.Events, x => x.Text == "hello");
        Assert.Contains(session.Events, x => x.Diagnostic?.Code == "malformed-record" && x.Native.Text == "{\"type\":");
        Assert.Contains(session.Events, x => x.Diagnostic?.Code == "input-error");
    }

    [Fact]
    public async Task ResultBeforeCallStillAssociatesAndMissingCallIsVisible()
    {
        using var directory = new TestDirectory();
        var path = directory.Write("unordered.jsonl", """
            {"type":"response_item","payload":{"type":"function_call_output","call_id":"c1","output":"result"}}
            {"type":"response_item","payload":{"type":"function_call","name":"example","call_id":"c1","arguments":"{}"}}
            {"type":"response_item","payload":{"type":"function_call_output","call_id":"orphan","output":"result"}}
            """);
        var session = await new TranscriptReader().LoadAsync(new(path));

        Assert.Equal(2, session.Tools.Count);
        Assert.False(Assert.Single(session.Tools, x => x.CorrelationKey == "c1").IsMissingCall);
        Assert.Equal(1, session.Statistics.ResultsWithoutCalls);
    }

    [Fact]
    public async Task ReusedCallIdsStayAmbiguousRatherThanLinkingArbitrarily()
    {
        using var directory = new TestDirectory();
        var path = directory.Write("ambiguous.jsonl", """
            {"type":"response_item","payload":{"type":"function_call","name":"example","call_id":"c1","arguments":"{\"a\":1,\"b\":2}"}}
            {"type":"response_item","payload":{"type":"function_call","name":"example","call_id":"c1","arguments":"{\"b\":2,\"a\":1}"}}
            {"type":"response_item","payload":{"type":"function_call_output","call_id":"c1","output":"result"}}
            """);
        var session = await new TranscriptReader().LoadAsync(new(path));

        Assert.True(Assert.Single(session.Tools).IsAmbiguous);
        Assert.Equal(1, session.Statistics.AmbiguousToolKeys);
        Assert.Equal(2, Assert.Single(session.Statistics.RepeatedTools).Count);
    }

    [Fact]
    public async Task UnsupportedDshVersionStaysRawWithoutPretendingItWasDecoded()
    {
        using var directory = new TestDirectory();
        var path = directory.Write("future.jsonl", "{\"type\":\"session\",\"version\":99,\"id\":\"future\"}\n{\"type\":\"turn/start\",\"data\":{\"turn\":1}}");
        var session = await new TranscriptReader().LoadAsync(new(path));

        Assert.Contains(session.Events, x => x.Diagnostic?.Code == "unsupported-version");
        Assert.Null(session.Statistics.TurnCount);
        Assert.Equal("99", session.Metadata.FormatVersion);
    }

    [Fact]
    public async Task FencedMarkdownHeadingsStayInsideToolOutputAndErrorsAreExplicit()
    {
        using var directory = new TestDirectory();
        var path = directory.Write("errors.md", """
            # Example
            **Session ID:** md-errors
            ---
            ## Assistant (Build · model · 1s)
            **Tool: bash**
            **Input:**
            ```json
            {"command":"example"}
            ```
            **Error:**
            ````
            ## User
            ```
            quoted output
            ````
            ---
            ## User
            Try again.
            """);
        var session = await new TranscriptReader().LoadAsync(new(path));

        Assert.Equal(3, session.Statistics.NativeRecords);
        Assert.Equal(1, session.Statistics.Errors);
        Assert.Equal(1, session.Statistics.UserMessageCount);
        Assert.Contains("## User", Assert.Single(session.Tools).Results[0].Text);
    }

    [Fact]
    public async Task CancellationAndLimitsAreHonored()
    {
        var reader = new TranscriptReader();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.LoadAsync(new(FormatTests.Fixture("codex.jsonl")), cancellationToken: cancellation.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.LoadAsync(new(FormatTests.Fixture("codex.jsonl")), new ReadOptions { MaxLoadedEvents = 2 }));
        var limited = await reader.LoadAsync(new(FormatTests.Fixture("codex.jsonl")), new ReadOptions { MaxRecordCharacters = 50 });
        Assert.Contains(limited.Events, x => x.Diagnostic?.Code == "input-error");
    }

    [Fact]
    public async Task StreamingLargeJsonDoesNotRequireAWholeDocumentBuffer()
    {
        using var directory = new TestDirectory();
        var path = directory.Write("large.json", "");
        const int count = 25000;
        await using (var writer = new StreamWriter(path))
        {
            await writer.WriteAsync("{\"info\":{\"id\":\"large\"},\"messages\":[");
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    await writer.WriteAsync(',');
                }

                await writer.WriteAsync(JsonSerializer.Serialize(new { id = "m" + i, type = "user", text = new string('x', 600) }));
            }
            await writer.WriteAsync("]}");
        }
        var reader = new TranscriptReader();
        var statistics = await SessionStatisticsBuilder.CalculateAsync(reader.ReadEventsAsync(new(path), new ReadOptions { MaxRecordCharacters = 1024, MaxLoadedEvents = 5 }));

        Assert.Equal(count, statistics.UserMessageCount);
        Assert.Equal(count + 1, statistics.NativeRecords);
        Assert.Equal(0, statistics.DiagnosticEvents);
    }

    [Fact]
    public async Task QueriesCanStopBeforeMalformedTailAndPreserveUnicode()
    {
        using var directory = new TestDirectory();
        var path = directory.Write("query.jsonl", "{\"type\":\"user\",\"uuid\":\"u\",\"sessionId\":\"s\",\"message\":{\"role\":\"user\",\"content\":\"Hello 世界 🌿\"}}\n{broken");
        var query = new EventQuery { Kinds = new HashSet<EventKind> { EventKind.Message }, Text = "世界" };
        await foreach (var item in query.ApplyAsync(new TranscriptReader().ReadEventsAsync(new(path))))
        {
            Assert.Equal("Hello 世界 🌿", item.Text);
            return;
        }
        Assert.Fail("No Unicode search match.");
    }

    [Fact]
    public async Task PatchRequestsExposeFilesWithoutClaimingSuccessfulEdits()
    {
        using var directory = new TestDirectory();
        var record = JsonSerializer.Serialize(new
        {
            type = "response_item",
            payload = new
            {
                type = "custom_tool_call",
                name = "apply_patch",
                call_id = "patch1",
                input = "*** Begin Patch\n*** Update File: a.cs\n@@\n-old\n+new\n*** Move to: b.cs\n*** End Patch"
            }
        });
        var path = directory.Write("patch.jsonl", record);
        var session = await new TranscriptReader().LoadAsync(new(path));

        Assert.Equal(new[] { "a.cs", "b.cs" }, session.Statistics.RequestedFiles);
        Assert.Empty(session.Statistics.ReportedFiles);
        Assert.Equal(1, session.Statistics.CallsWithoutResults);
    }

    [Fact]
    public async Task DuplicateSettingsDoNotRewindTheActiveModel()
    {
        using var directory = new TestDirectory();
        const string first = "{\"type\":\"turn_context\",\"ordinal\":1,\"payload\":{\"model\":\"first\"}}\n";
        var path = directory.Write("duplicate-settings.jsonl", first +
            "{\"type\":\"turn_context\",\"ordinal\":2,\"payload\":{\"model\":\"second\"}}\n" + first +
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"Done.\"}]}}\n");
        var session = await new TranscriptReader().LoadAsync(new(path));

        Assert.Equal(1, session.Statistics.DuplicateEvents);
        Assert.Equal("second", session.Metadata.Model);
        Assert.Equal("second", Assert.Single(session.Events, x => x.Kind == EventKind.Message).Model);
    }
}
