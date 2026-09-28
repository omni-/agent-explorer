using AgentExplorer.Core.Analysis;
using AgentExplorer.Core.Models;

using Xunit;

namespace AgentExplorer.Core.Tests;

public sealed class StatisticsTests
{
    [Fact]
    public async Task StreamingAndLoadedFactsAreIdenticalAndDeterministic()
    {
        var reader = new TranscriptReader();
        var input = new TranscriptInput(FormatTests.Fixture("codex.jsonl"));
        var loaded = await reader.LoadAsync(input);
        var streamed = await SessionStatisticsBuilder.CalculateAsync(reader.ReadEventsAsync(input));

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(loaded.Statistics), System.Text.Json.JsonSerializer.Serialize(streamed));
        Assert.Equal(19, loaded.NativeRecords.Count);
        Assert.Equal(2, streamed.ToolUsageByLayer[ActivityLayer.Model].Values.Sum());
    }

    [Fact]
    public async Task ContextSnapshotsAreNotSummedAndCounterRegressionsAreVisible()
    {
        using var directory = new TestDirectory();
        var path = directory.Write("usage.jsonl", """
            {"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":20,"output_tokens":4},"model_context_window":1000}}}
            {"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":30,"output_tokens":6},"model_context_window":1000}}}
            {"type":"event_msg","payload":{"type":"token_count","info":null}}
            {"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":10,"output_tokens":2},"model_context_window":2000}}}
            """);
        var session = await new TranscriptReader().LoadAsync(new(path, TranscriptFormat.CodexJsonl));

        var usage = Assert.Single(session.Statistics.Usage);
        Assert.Equal(10, usage.Tokens.Input);
        Assert.Equal(2, usage.Tokens.Output);
        Assert.Null(usage.Tokens.Total);
        Assert.Equal(2000, usage.MaximumContextWindow);
        Assert.Equal(1, usage.CounterRegressions);
    }

    [Fact]
    public async Task MultipleAssistantBlocksShareNativeMessageIdentity()
    {
        using var directory = new TestDirectory();
        var path = directory.Write("claude.jsonl", """
            {"type":"assistant","uuid":"block1","sessionId":"s","message":{"id":"message1","role":"assistant","content":[{"type":"thinking","thinking":"Consider."}]}}
            {"type":"assistant","uuid":"block2","sessionId":"s","message":{"id":"message1","role":"assistant","content":[{"type":"text","text":"Answer."}]}}
            {"type":"assistant","uuid":"block3","sessionId":"s","message":{"id":"message2","role":"assistant","content":[{"type":"tool_use","name":"Read","id":"t1","input":{"file_path":"a.cs"}}]}}
            """);
        var session = await new TranscriptReader().LoadAsync(new(path));

        Assert.Equal(2, session.Statistics.AssistantMessageCount);
        Assert.Equal(1, session.Statistics.CallsWithoutResults);
    }
}
