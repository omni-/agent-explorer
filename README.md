# Agent Explorer Core

A read-only, local C# library for coding-agent transcripts. It discovers sessions, reads native records, produces normalized events, links tool calls and results, searches content, and calculates mechanical session facts. There is no TUI, web UI, model judge, telemetry, agent execution, or network access in the Core.

The solution targets **.NET 10**. Its only runtime package is the managed `ZstdSharp.Port` decoder for DSH's compressed logs. Build/test packages are restored through NuGet; running the library requires no network, Python, Node, agent installation, or account.

## Build and verify

```powershell
dotnet restore AgentExplorer.slnx
dotnet build AgentExplorer.slnx --no-restore
dotnet test AgentExplorer.slnx --no-restore
dotnet format AgentExplorer.slnx --verify-no-changes --no-restore
```

The tests use small, authored, sanitized fixtures. Private real transcripts under `data/` and temporary verification outputs under `.local/` are ignored by Git.

## Use the library

Reference `src/AgentExplorer.Core/AgentExplorer.Core.csproj` from a .NET consumer:

```csharp
using AgentExplorer.Core;
using AgentExplorer.Core.Discovery;
using AgentExplorer.Core.Models;
using AgentExplorer.Core.Querying;

// No transcript paths are needed for normal Codex/Claude installations.
var available = await new SessionDiscovery().DiscoverAsync();
var descriptor = available.Sessions.First();
var session = await new TranscriptReader().LoadAsync(descriptor.PreferredInput);

foreach (var item in session.Query(new EventQuery { Text = "build" }))
{
    Console.WriteLine($"{item.Timestamp}: {item.Kind}: {item.Text}");
    // Native.Text and its source location are available on every event.
}

var facts = session.Statistics;
var unresolved = session.Tools.Where(tool => tool.IsMissingResult);
```

For large sessions, use `ReadEventsAsync` and `SessionStatisticsBuilder.CalculateAsync` rather than loading all events. See the [consumer API and contracts](docs/core-api.md).

## Supported inputs

| Source | Inputs exercised successfully | Discovery |
|---|---|---|
| Codex | Local rollout JSONL, older and current observed schemas | `$CODEX_HOME` or `~/.codex`, including archived sessions |
| Claude Code | Local project/session JSONL | `$CLAUDE_CONFIG_DIR` or `~/.claude`, across projects |
| OpenCode V1 | Canonical `{ info, messages: [{ info, parts }] }` JSON envelope | Configured export directories |
| OpenCode V2 | Structured JSON exports and Markdown `/export` transcripts | Configured export directories; JSON preferred for matching IDs |
| DSH | Storage v3 JSONL, concatenated Zstandard frames, ZIP bundles with root and subagent sessions | `$DSH_HOME` or `~/.dsh`, plus configured export directories |

V1 live verification used real database records in the canonical export envelope, reconstructed read-only without starting an agent or migrating its database. DSH generations other than v3 are preserved as unknown records with a warning; they are not claimed as supported decoders.

## Diagnostic CLI

```powershell
dotnet run --project src/AgentExplorer.Cli -- discover
dotnet run --project src/AgentExplorer.Cli -- discover C:\transcript-exports
dotnet run --project src/AgentExplorer.Cli -- stats C:\transcripts\rollout.jsonl
dotnet run --project src/AgentExplorer.Cli -- events C:\transcripts\session.json build
dotnet run --project src/AgentExplorer.Cli -- verify C:\transcripts\session.zip
dotnet run --project src/AgentExplorer.Cli -- stats C:\transcripts\session.zip DshJsonl subagents/child/session.v3.jsonl
```

`events` emits JSONL including native content. `stats` and `verify` emit facts and a native-record hash. `verify` exits with 2 for parser warnings, 1 for an input/argument failure, and 0 for a successfully read input; it does not judge the agent's work. Informational notices about Markdown fidelity do not fail verification.

Read [the encountered formats and their limitations](docs/transcript-formats.md) and [the verification account](docs/task-notes/2026-09-28-core-verification.md) before treating counts from different runtimes as directly comparable.

The next useful backend task is a revision-aware local index for incremental rescans, cross-session queries, and pagination, built on these native-record and event contracts.
