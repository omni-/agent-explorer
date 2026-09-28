# Agent Explorer Core verification — 2026-09-28

## Delivered

The repository contains a .NET 10 library, a minimal diagnostic CLI, 32 passing tests, sanitized fixtures, format notes, consumer contracts, and an optional independent local audit script. It contains no product UI, model-quality scoring, hosted service, agent execution, or telemetry.

The existing `AGENTS.md` and real `data/` transcripts were not edited. Private transcripts and temporary audit snapshots are ignored by Git. New authored fixtures are under `tests/AgentExplorer.Core.Tests/Fixtures`.

## Environment and inspected versions

Verification ran on Windows 10 x64 with .NET SDK 10.0.401. The Core and CLI built with zero compiler/analyzer warnings or errors. `dotnet format AgentExplorer.slnx --verify-no-changes --no-restore` passed against the whole solution.

| Source | Real formats and versions inspected | Fully audited inputs |
|---|---|---|
| Codex | Rollouts containing CLI versions from `0.145.0-alpha.30` through `0.158.0-alpha.2.1`; metadata and native event types inspected across 423 active-session files | 5 files: `0.145.0-alpha.30`, `0.149.0-alpha.4.3`, `0.154.0-alpha.6.2`, `0.155.0-alpha.9.2`, `0.158.0-alpha.2.1` |
| Claude Code | Project JSONL, versions from `2.1.219` through `2.1.281`; messages, tool blocks, thinking, usage, attachments, backups and system records | 4 input files, including one copied transcript: `2.1.219`, `2.1.235`/`2.1.237` in one resumed file, and `2.1.281` |
| OpenCode V1 | Retained session/message/part rows with session versions `1.18.18`, `.21`, `.23`, `.26`, `.27`, `.29`, `.30`, `.31`; canonical export shape checked against the V1 exporter | 3 read-only reconstructed canonical JSON envelopes: `1.18.18`, `1.18.21`, `1.18.31` |
| OpenCode V2 JSON | Directly typed messages/content, tool state, session/message totals and model/provider metadata; installed CLI package `2.0.18` | 1 supplied structured JSON export; its producing app version is absent |
| OpenCode V2 Markdown | Role/model/duration headings, thinking, JSON tool input, output and error blocks | 1 supplied Markdown export with the same session ID as the JSON |
| DSH | Storage version 3, raw JSONL, concatenated-frame Zstandard files, and ZIP export with subagent logs; local container recipe pins `0.1.5-rc.1` | 22 session inputs: 1 standalone JSONL, 7 locally persisted compressed logs, and 14 members of one ZIP |

The exact DSH application version that produced each session is not stored in its header. The container recipe is supporting installation context, not proof of the producing version. Upstream persistence/frame code was also inspected to confirm the concatenated-frame representation. No DSH schema migration was added, and other generations are not claimed as decoded support.

OpenCode V1's local records were wrapped in the native `{ info, messages: [{ info, parts }] }` envelope using read-only SQLite queries. This is real-data parser verification, **not a claim that a V1 CLI export command was run**. The Core does not access that database. This avoided letting a different installed runtime modify or migrate it.

## Independent real-data audit

The reproducible audit is:

```powershell
dotnet build AgentExplorer.slnx
python scripts/verify_local.py --user-sessions --opencode-v1-db "$env:USERPROFILE\.local\share\opencode\opencode.db"
```

Python 3.14's standard library independently decoded JSON, ZIP and concatenated Zstandard streams. For each input, the audit compared:

1. source-file SHA-256 before and after reading;
2. exact decoded native record text against the CLI's record hash;
3. native record counts (JSON property/message slices for structured exports);
4. tool-call counts independently selected from the native schema;
5. response and available session token input/output counters using native identity keys;
6. absence of parser warnings and failed reads.

All **36 input reads passed**, covering **16,864 native records** and **29,252 normalized events**. “36” counts inputs, including ZIP members and a copied Claude transcript; it does not mean 36 unique sessions. All tool calls/results in this live selection had matching keys. Missing, ambiguous, reversed and interrupted relationships were exercised separately in fixtures.

| Format | Audited inputs | Native records | Normalized events |
|---|---:|---:|---:|
| Codex JSONL | 5 | 3,022 | 3,362 |
| Claude Code JSONL | 4 | 4,458 | 6,075 |
| OpenCode JSON, V1 and V2 | 4 | 1,280 | 8,313 |
| OpenCode Markdown | 1 | 101 sections | 243 |
| DSH v3, all containers | 22 | 8,003 | 11,259 |

The private per-input audit report, hashes and timings are in `.local/live-verification.json`. No conversation bodies are copied into that report. The script also generates ignored V1 verification snapshots under `.local/live`.

The supplied OpenCode JSON had 395 tool calls. Markdown for the same ID had 95, including two explicit errors. After accounting for its information-only fidelity notice, Markdown parsed without warnings. This established a real coverage difference, not just a schema difference.

## Automatic discovery

A read-only default discovery plus the supplied `data/` export directory returned:

| Source | Session identities | Separately loadable inputs |
|---|---:|---:|
| Codex | 890 | 1,455, including archives/copies |
| Claude Code | 52 | 73, including the supplied copy and all 72 local project JSONL files |
| DSH | 22 | 22 |
| OpenCode | 1 | 2, with structured JSON preferred |

It returned zero discovery issues during that run. These are observations from this machine, not stable counts guaranteed by the library. Discovery's bounded metadata reads are not presented as full-content verification of all 1,552 candidate inputs.

Tests independently verify environment overrides, explicit home overrides, absent roots, multiple projects, archived Codex sessions, JSON preference, and ZIP child discovery.

## Automated test coverage

`dotnet test AgentExplorer.slnx --no-restore` passed **32 tests**, with zero skipped tests. They exercise the actual readers, filesystem and decoders:

- Detection and loading for all five format families, both OpenCode JSON shapes, and all DSH containers.
- Source hashes, exact native JSONL text including line endings, unknown native fields, encrypted/signature/stream data and JSON pointers.
- Stable chronological ordering, original sequences, missing timestamps, and Unicode search.
- Calls/results, multiple calls per message, results before calls, missing results/calls, reused IDs and DSH request/execution views.
- Claude response usage deduplication, cumulative snapshots and regressions, context capacity, costs, message identity, command exits and file-request distinctions.
- Malformed/scalar JSONL, partial JSON, unsupported DSH generations, damaged compressed tails, Markdown errors/fences, cancellation and configured limits.
- Duplicate settings that must not rewind the active model or latest session metadata.
- Streaming a generated 25,000-message JSON export with a 1,024-character record limit and a full-load limit of only 5 events, demonstrating that streaming does not buffer the whole document.

The compaction variants in the Claude fixture are authored from known schema conventions; the live sample audit did not contain every Claude compaction variant. DSH v3 live records exposed context configuration and boundaries, but did not establish all possible compaction event spellings. Unknown variants are preserved for subsequent decoder work.

## Large-session check

An additional generated OpenCode export contained **200,000 messages**, each with 1,000 content characters, totaling **208,088,933 bytes**. Streaming CLI analysis returned 200,001 native records, 200,000 user messages and zero diagnostics. The observed run took **25.1 seconds**, with a sampled peak process working set of **133,402,624 bytes** (about 127 MiB), including the .NET runtime. This was a Debug build on the same Windows machine and ran alongside the local audit; it is a measurement, not a comparative benchmark verdict.

The 208 MB temporary input was removed after the check. Its small result and measurement summaries remain under `.local/`. The largest real transcript audited was a 24.6 MB Claude JSONL file; a real 11.6 MB Codex rollout was also included.

## Meaningful limitations and next task

- Markdown cannot recover omitted messages, timestamps, IDs, usage, provider metadata or structured process statuses. Its delimiter format is not unambiguous for every possible quoted body.
- Hidden/encrypted reasoning remains opaque. Missing tool results and process exits remain unknown. Tool success flags alone do not prove command success.
- Different usage series and event layers overlap. They must not be summed into one “total” without a source-aware policy.
- Discovery uses bounded prefixes and groups same native identities without merging revisions or histories. OpenCode uses explicit export locations; the Core does not couple to its V2 internal store.
- Full loads and sorting use memory proportional to loaded content. Streaming retains identifier-sized duplicate/correlation/accounting state and has configurable input/record limits.
- A broken outer JSON structure or unreadable compressed tail may prevent recovery beyond the last complete records. Original files remain available and unchanged.
- Native fields outside the common model remain inspectable rather than being guessed into misleading common fields. No shell/JavaScript evaluation or attachment fetching occurs.
- Windows was exercised directly. The library uses portable .NET APIs, but this task did not run its tests on Linux or macOS.

The most useful next backend task is a **revision-aware local index**: incremental discovery/rescanning, stable stored input revisions, source-preserving event pagination, and cross-session queries. Keep index schema/normalization versioning separate from immutable source files and retain the current uncertainty and accounting rules.
