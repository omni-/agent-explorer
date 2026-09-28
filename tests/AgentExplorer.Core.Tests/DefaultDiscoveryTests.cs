using AgentExplorer.Core.Discovery;
using AgentExplorer.Core.Models;

using Xunit;

namespace AgentExplorer.Core.Tests;

[Collection("Environment")]
public sealed class DefaultDiscoveryTests
{
    [Fact]
    public async Task DefaultDiscoveryUsesEnvironmentOverridesWithoutTranscriptPaths()
    {
        using var directory = new TestDirectory();
        directory.Write("codex/sessions/project/rollout-example.jsonl", await File.ReadAllTextAsync(FormatTests.Fixture("codex.jsonl")));
        directory.Write("claude/projects/project-one/example.jsonl", await File.ReadAllTextAsync(FormatTests.Fixture("claude.jsonl")));
        var codex = Environment.GetEnvironmentVariable("CODEX_HOME");
        var claude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(directory.Path, "codex"));
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(directory.Path, "claude"));
            var result = await new SessionDiscovery().DiscoverAsync(new DiscoveryOptions { DshHome = "" });

            Assert.Empty(result.Issues);
            Assert.Single(result.Sessions, x => x.Source == AgentSource.Codex);
            Assert.Single(result.Sessions, x => x.Source == AgentSource.ClaudeCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", codex);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claude);
        }
    }
}
