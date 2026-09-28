using System.Security.Cryptography;

using AgentExplorer.Core.Models;
using AgentExplorer.Core.Querying;

using Xunit;

namespace AgentExplorer.Core.Tests;

public sealed class FormatTests
{
    [Theory]
    [InlineData("codex.jsonl", TranscriptFormat.CodexJsonl, "codex-test")]
    [InlineData("claude.jsonl", TranscriptFormat.ClaudeCodeJsonl, "claude-test")]
    [InlineData("dsh.jsonl", TranscriptFormat.DshJsonl, "dsh-test")]
    [InlineData("opencode-v1.json", TranscriptFormat.OpenCodeJson, "opencode-v1-test")]
    [InlineData("opencode-v2.json", TranscriptFormat.OpenCodeJson, "opencode-v2-test")]
    [InlineData("opencode-v2.md", TranscriptFormat.OpenCodeMarkdown, "opencode-v2-test")]
    public async Task DetectsLoadsAndNeverModifiesFixtures(string file, TranscriptFormat format, string id)
    {
        // Arrange
        var path = Fixture(file);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var reader = new TranscriptReader();

        // Act
        var detected = await reader.DetectFormatAsync(new(path));
        var session = await reader.LoadAsync(new(path));

        // Assert
        Assert.Equal(format, detected);
        Assert.Equal(id, session.Metadata.SessionId);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        Assert.DoesNotContain(session.Events, x => x.Diagnostic is { Severity: "warning" or "error" });
        Assert.All(session.Events, x => Assert.Equal(System.IO.Path.GetFullPath(path), x.Native.Location.Path));
    }

    [Fact]
    public async Task CodexPreservesRawRecordsMirrorsAndExecutionLayers()
    {
        var path = Fixture("codex.jsonl");
        var session = await new TranscriptReader().LoadAsync(new(path));

        Assert.Equal(19, session.Statistics.NativeRecords);
        Assert.Equal(1, session.Statistics.DuplicateEvents);
        Assert.Equal(1, session.Statistics.Compactions);
        Assert.Equal(1, session.Statistics.TurnCount);
        Assert.Equal(1, session.Statistics.CallsWithoutResults);
        Assert.Equal(1, session.Statistics.Commands["execution-events"].ExitCodeSuccesses);
        Assert.Equal(1, session.Statistics.Commands["shell-tools"].ExitCodeSuccesses);
        Assert.Equal(TimeSpan.FromSeconds(12), session.Statistics.ObservedDuration);
        Assert.Contains("C:\\sample\\a.cs", session.Statistics.ReportedFiles);
        var reason = Assert.Single(session.Events, x => x.Kind == EventKind.Reasoning);
        using var native = reason.Native.ParseJson();
        Assert.Equal("opaque-test-payload", native.RootElement.GetProperty("payload").GetProperty("encrypted_content").GetString());
        Assert.Equal("Check the file.", reason.Text);
        var bytesAsText = await File.ReadAllTextAsync(path);
        Assert.Equal(bytesAsText, string.Concat(session.Events.OrderBy(x => x.Sequence).DistinctBy(x => x.Native.Location.RecordIndex).Select(x => x.Native.Text)));
        var timestamps = session.Events.Where(x => x.Timestamp is not null).Select(x => x.Timestamp).ToArray();
        Assert.Equal(timestamps.Order(), timestamps);
        Assert.Contains(session.Events, x => x.Kind == EventKind.Unknown && x.Timestamp is null);
        Assert.Equal(13, Assert.Single(session.Statistics.Usage, x => x.Series == "codex.thread").Tokens.Total);
    }

    [Fact]
    public async Task ClaudeResponseUsageIsNotSummedAcrossBlocksAndToolResultsAreNotUserTurns()
    {
        var session = await new TranscriptReader().LoadAsync(new(Fixture("claude.jsonl")));

        var usage = Assert.Single(session.Statistics.Usage, x => x.Series == "claude.response");
        Assert.Equal(2, usage.Samples);
        Assert.Equal(15, usage.Tokens.Input);
        Assert.Equal(7, usage.Tokens.Output);
        Assert.Null(usage.Tokens.Total);
        Assert.Equal(1, session.Statistics.UserMessageCount);
        Assert.Null(session.Statistics.TurnCount);
        Assert.Equal(2, session.Tools.Count);
        Assert.All(session.Tools, x => { Assert.False(x.IsMissingCall); Assert.False(x.IsMissingResult); });
        Assert.Equal(0, session.Statistics.Commands["shell-tools"].ExitCodeSuccesses);
        Assert.Equal(1, session.Statistics.Commands["shell-tools"].UnknownExitCode);
        Assert.Equal(2, session.Statistics.Errors);
        Assert.Equal(0.25m, Assert.Single(session.Statistics.Usage, x => x.Series == "claude.cost").Cost);
        Assert.Equal("Sample session", session.Metadata.Title);
    }

    [Fact]
    public async Task DshUsesDurableCallsAndPreservesStreamsAndSourceSequences()
    {
        var session = await new TranscriptReader().LoadAsync(new(Fixture("dsh.jsonl")));

        Assert.Equal(2, session.Statistics.ToolUsage["read"]);
        Assert.Equal(2, session.Tools.Count);
        Assert.Equal(1, session.Statistics.MirrorEvents);
        Assert.Equal(1, session.Statistics.Errors);
        Assert.Equal(2, Assert.Single(session.Statistics.RepeatedTools).Count);
        Assert.Equal("parent-example", session.Metadata.ParentSessionId);
        Assert.Equal(0, session.Statistics.Commands["execution-events"].Observations);
        var reasoning = Assert.Single(session.Events, x => x.Kind == EventKind.Reasoning);
        using var original = reasoning.Native.ParseJson();
        Assert.Equal("chunk", original.RootElement.GetProperty("data").GetProperty("stream")[0].GetProperty("type").GetString());
        var result = session.Events.First(x => x.Kind == EventKind.ToolResult);
        using var resultOriginal = result.Native.ParseJson();
        Assert.Equal(6, resultOriginal.RootElement.GetProperty("sourceEventSeqs")[0].GetInt32());
        Assert.Equal(33, Assert.Single(session.Statistics.Usage, x => x.Series == "dsh.response").Tokens.Total);
        Assert.Equal(100000, Assert.Single(session.Statistics.Usage, x => x.Series == "dsh.context").MaximumContextWindow);
    }

    [Fact]
    public async Task OpenCodeJsonVersionsNormalizeToTheSameToolConcepts()
    {
        var reader = new TranscriptReader();
        var v1 = await reader.LoadAsync(new(Fixture("opencode-v1.json")));
        var v2 = await reader.LoadAsync(new(Fixture("opencode-v2.json")));

        Assert.Equal("v1-export", v1.Metadata.FormatVersion);
        Assert.Equal("v2-export", v2.Metadata.FormatVersion);
        var first = Assert.Single(v1.Tools);
        var second = Assert.Single(v2.Tools);
        Assert.Equal(first.Calls[0].Tool, second.Calls[0].Tool);
        Assert.Equal(first.Results[0].Tool, second.Results[0].Tool);
        Assert.Equal(1, v1.Statistics.Commands["shell-tools"].ExitCodeSuccesses);
        Assert.Equal(1, v2.Statistics.Commands["shell-tools"].ExitCodeSuccesses);
        Assert.Single(v1.Statistics.Usage);
        Assert.Contains(v1.Events, x => x.Native.Location.JsonPointer == "/futureProperty" && x.Kind == EventKind.Unknown);
        Assert.Contains(v1.Events, x => x.Kind == EventKind.Compaction);
        Assert.Contains(v1.Events, x => x.IsError);
    }

    [Fact]
    public async Task MarkdownNormalizesContentWithoutInventingTimestampsUsageOrNativeIds()
    {
        var session = await new TranscriptReader().LoadAsync(new(Fixture("opencode-v2.md")));

        Assert.All(session.Events, x => Assert.Null(x.Timestamp));
        Assert.Empty(session.Statistics.Usage);
        Assert.Null(session.Statistics.ObservedDuration);
        var tool = Assert.Single(session.Tools);
        Assert.Null(tool.Calls[0].Tool!.CallId);
        Assert.Equal("bash", tool.Calls[0].Tool!.Name);
        Assert.Equal("sample", tool.Results[0].Text);
        Assert.Equal(OperationOutcome.Unknown, tool.Results[0].Tool!.Outcome);
        Assert.Contains(session.Events, x => x.Kind == EventKind.Reasoning && x.Text == "Check sample.");
        Assert.Single(session.Query(new EventQuery { Text = "inspect", Role = "user" }));
        Assert.NotEmpty(session.Query(new EventQuery { Text = "Session ID", SearchNativeData = true }));
    }

    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
