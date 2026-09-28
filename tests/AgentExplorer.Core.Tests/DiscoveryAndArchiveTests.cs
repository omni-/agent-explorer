using System.IO.Compression;
using System.Text;

using AgentExplorer.Core.Discovery;
using AgentExplorer.Core.Models;

using Xunit;

using ZstdSharp;

namespace AgentExplorer.Core.Tests;

public sealed class DiscoveryAndArchiveTests
{
    [Fact]
    public async Task DshZipLoadsRootAndDiscoversChildSessionsWithoutExtraction()
    {
        var root = await new TranscriptReader().LoadAsync(new(FormatTests.Fixture("dsh-bundle.zip")));
        var child = await new TranscriptReader().LoadAsync(new(FormatTests.Fixture("dsh-bundle.zip"), ArchiveEntry: "subagents/child/session.v3.jsonl"));

        Assert.Equal("dsh-test", root.Metadata.SessionId);
        Assert.Equal("dsh-child", child.Metadata.SessionId);
        Assert.Equal("dsh-test", child.Metadata.ParentSessionId);
        Assert.All(root.Events, e => Assert.Equal("session.v3.jsonl", e.Native.Location.ArchiveEntry));
        Assert.All(child.Events, e => Assert.Equal("subagents/child/session.v3.jsonl", e.Native.Location.ArchiveEntry));
        var discovery = await new SessionDiscovery().DiscoverAsync(new DiscoveryOptions { UseDefaultLocations = false, ExportDirectories = [Path.GetDirectoryName(FormatTests.Fixture("dsh-bundle.zip"))!] });
        Assert.Contains(discovery.Sessions, x => x.Metadata.SessionId == "dsh-child");
        Assert.DoesNotContain(discovery.Issues, x => x.Path.EndsWith("dsh-bundle.zip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcatenatedZstandardFramesMatchUncompressedSession()
    {
        var reader = new TranscriptReader();
        var plain = await reader.LoadAsync(new(FormatTests.Fixture("dsh.jsonl")));
        var compressed = await reader.LoadAsync(new(FormatTests.Fixture("session.v3.jsonl.zstd")));

        Assert.Equal(plain.Events.Count, compressed.Events.Count);
        Assert.Equal(plain.Events.Select(x => x.Native.Text), compressed.Events.Select(x => x.Native.Text));
        Assert.Equal(plain.Statistics.ToolUsage, compressed.Statistics.ToolUsage);
        Assert.DoesNotContain(compressed.Events, x => x.Diagnostic is not null);
    }

    [Fact]
    public async Task DamagedZstandardTailReportsFailureAndKeepsEarlierRecords()
    {
        using var directory = new TestDirectory();
        var path = directory.Write("session.v3.jsonl.zstd", "");
        using var compressor = new Compressor();
        var header = Encoding.UTF8.GetBytes("{\"type\":\"session\",\"version\":3,\"id\":\"broken\"}\n");
        var first = compressor.Wrap(header).ToArray();
        var tail = compressor.Wrap(Encoding.UTF8.GetBytes("{\"type\":\"turn/start\",\"seq\":1,\"data\":{\"turn\":1}}\n")).ToArray();
        await File.WriteAllBytesAsync(path, [.. first, .. tail.Take(tail.Length / 2)]);
        var session = await new TranscriptReader().LoadAsync(new(path));

        Assert.Equal("broken", session.Metadata.SessionId);
        Assert.Contains(session.Events, x => x.Diagnostic?.Code is "input-error" or "malformed-record");
    }

    [Fact]
    public async Task ConfiguredHomesDiscoverAcrossProjectsAndPreferJsonExports()
    {
        using var directory = new TestDirectory();
        directory.Write("codex/sessions/2026/09/20/rollout-a.jsonl", await File.ReadAllTextAsync(FormatTests.Fixture("codex.jsonl")));
        directory.Write("codex/archived_sessions/rollout-b.jsonl", (await File.ReadAllTextAsync(FormatTests.Fixture("codex.jsonl"))).Replace("codex-test", "codex-archive", StringComparison.Ordinal));
        directory.Write("claude/projects/project-one/a.jsonl", await File.ReadAllTextAsync(FormatTests.Fixture("claude.jsonl")));
        directory.Write("claude/projects/project-two/b.jsonl", (await File.ReadAllTextAsync(FormatTests.Fixture("claude.jsonl"))).Replace("claude-test", "claude-second", StringComparison.Ordinal));
        directory.Write("exports/session.json", await File.ReadAllTextAsync(FormatTests.Fixture("opencode-v2.json")));
        directory.Write("exports/session.md", await File.ReadAllTextAsync(FormatTests.Fixture("opencode-v2.md")));
        var options = new DiscoveryOptions
        {
            UseDefaultLocations = false,
            CodexHome = Path.Combine(directory.Path, "codex"),
            ClaudeHome = Path.Combine(directory.Path, "claude"),
            ExportDirectories = [Path.Combine(directory.Path, "exports")]
        };
        var result = await new SessionDiscovery().DiscoverAsync(options);

        Assert.Empty(result.Issues);
        Assert.Equal(5, result.Sessions.Count);
        Assert.Equal(2, result.Sessions.Count(x => x.Source == AgentSource.Codex));
        Assert.Equal(2, result.Sessions.Count(x => x.Source == AgentSource.ClaudeCode));
        var openCode = Assert.Single(result.Sessions, x => x.Source == AgentSource.OpenCode);
        Assert.Equal(TranscriptFormat.OpenCodeJson, openCode.PreferredInput.Format);
        Assert.Equal(TranscriptFormat.OpenCodeMarkdown, Assert.Single(openCode.AlternativeInputs).Format);
    }

    [Fact]
    public async Task NoDefaultLocationsAndAbsentRootsProduceEmptyResult()
    {
        using var directory = new TestDirectory();
        var result = await new SessionDiscovery().DiscoverAsync(new DiscoveryOptions
        {
            UseDefaultLocations = false,
            CodexHome = directory.Path,
            ClaudeHome = directory.Path,
            DshHome = directory.Path
        });

        Assert.Empty(result.Sessions);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task ArchiveEntriesNeverBecomeFilesystemPaths()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "unsafe-names.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../../outside/session.v3.jsonl");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("{\"type\":\"session\",\"version\":3,\"id\":\"safe-read\"}\n");
        }
        var session = await new TranscriptReader().LoadAsync(new(path));

        Assert.Equal("safe-read", session.Metadata.SessionId);
        Assert.Single(Directory.EnumerateFileSystemEntries(directory.Path));
        Assert.Equal("../../outside/session.v3.jsonl", session.Events[0].Native.Location.ArchiveEntry);
    }
}
