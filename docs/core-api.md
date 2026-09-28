# Consumer API and contracts

## Entry points

- `SessionDiscovery.DiscoverAsync`: available session identities, preferred inputs, alternatives, and discovery issues.
- `TranscriptReader.DetectFormatAsync`: bounded content detection; an explicit `TranscriptInput.Format` bypasses detection.
- `TranscriptReader.ReadEventsAsync`: asynchronous events in source record order.
- `TranscriptReader.LoadAsync`: a session with metadata, chronological events, native records, tool relationships, and statistics.
- `EventQuery`: composable property filters and literal content search, for loaded or streaming events.
- `SessionStatisticsBuilder`: incremental factual aggregates without retaining conversation bodies.

`TranscriptReader` and `SessionDiscovery` contain no per-session mutable state and can be shared. Create a separate statistics builder for each input. The Core does not own a logger or a database; callers receive diagnostics as data and decide how to display or log them.

## Discovery

```csharp
var result = await new SessionDiscovery().DiscoverAsync(new DiscoveryOptions
{
    CodexHome = @"D:\agent-data\codex",
    ClaudeHome = @"D:\agent-data\claude",
    ExportDirectories = [@"D:\exports", @"C:\projects\example\transcripts"]
});
```

Without overrides, the roots are:

| Source | Root selection | Traversed locations |
|---|---|---|
| Codex | `CODEX_HOME`, otherwise user profile + `.codex` | `sessions/**/rollout-*.jsonl`, `archived_sessions/**/rollout-*.jsonl` |
| Claude Code | `CLAUDE_CONFIG_DIR`, otherwise user profile + `.claude` | `projects/**/*.jsonl` |
| DSH | `DSH_HOME`, otherwise user profile + `.dsh` | `sessions/**/session*.jsonl[.zstd]` |
| Exports | Explicit `ExportDirectories` | JSON, JSONL, Markdown, ZIP, and Zstandard candidates |

An explicit home overrides its environment variable. An empty home disables that source. `UseDefaultLocations = false` disables all implicit homes while preserving explicit overrides and export directories. Windows uses the current user's profile directory; paths are not hard-coded to a particular account. Missing roots are normal. Unreadable candidates or directories generate issues. Symbolic links and reparse points below a discovery root are skipped, preventing traversal loops.

Discovery reads at most `MetadataEventLimit` normalized events per candidate (default 32). Metadata is therefore explicitly partial: titles, model changes, or settings near the end require a full load. It does not run an agent, inspect credentials, query an internal event store, or migrate any files.

Descriptors group inputs by source and **explicit native session ID**. Missing IDs are kept separate by file/member path; the Core never invents a session ID from a filename. Same-ID inputs may be copies, partial exports, or different revisions. They remain separately loadable in `AlternativeInputs`. They are not merged or assumed byte-identical. OpenCode structured JSON outranks Markdown. Equal-fidelity alternatives are ordered deterministically by path, not guessed to be the latest or most complete.

ZIP discovery lists each JSONL member as a candidate, including DSH subagents. `LoadAsync(new TranscriptInput(zipPath))` chooses the single root member when present. Set `ArchiveEntry` for another member or an ambiguous bundle. Members are streamed in place, never extracted, and `ParentSessionId` comes from the native header.

## Events and native data

An event includes source, format, kind, optional timestamp, native type, record-local ID, role, message/turn/step IDs, model/provider, text, and optional tool, command, file, usage, metadata, or diagnostic facts.

`NativeRecord` contains the original decoded text, a full file path, optional ZIP member, physical record index, line number when available, and a JSON pointer for streamed export values. Expanded events share the same `NativeRecord` instance. `TranscriptEvent.NativePointer` identifies the relevant subvalue *inside that record*. `NativeRecord.ParseJson()` returns a caller-owned `JsonDocument`. Dispose it after inspection.

For JSONL, raw records include their original line terminators and whitespace. Markdown records are consecutive, unchanged sections. Concatenating those native records reproduces decoded source text, apart from a UTF byte-order mark consumed by the text reader. OpenCode JSON is framed into root property values and individual messages; each value's exact text is preserved, including unrecognized root properties. The surrounding object/array punctuation and inter-value whitespace remain in the original file, accessible through `SourceLocation.Path`. The Core never rewrites that file. ZIP assets outside session records remain in the original archive and are not loaded automatically.

IDs such as `r12:2` identify the third normalized event derived from native record 12 within one input. They remain stable while that input's record prefix remains unchanged; they are not global database IDs. `NativeId`, `MessageId`, and `Tool.CallId` retain the distinct native identities where available.

Unknown native types become `Unknown` events. Additional fields on known types remain in `Native.Text`. Image data, encrypted reasoning, signatures, stream chunks, source event references, snapshots, and provider metadata are not discarded or interpreted as executable content.

## Ordering and uncertainty

Streaming events keep native record order, then deterministic expansion order. `Sequence` always reflects that order. This avoids unbounded buffering while reading a live or very large log.

Full loads order events by explicit timestamps, then `Sequence`. An event with no timestamp uses the previous source event's timestamp only as a sorting anchor; leading untimed events come first. Its public `Timestamp` stays null. This placement is a convention, not a recovered time. Set `ReadOptions.OrderByTimestamp = false` to retain source order. Out-of-order timestamps, equal timestamps, and multiple events expanded from one native record are therefore inspectable without losing their original sequence.

Numeric timestamps are interpreted only according to supported source fields. Offset-free Markdown dates do not become UTC times using the machine's local timezone. Provider names, costs, command exits, missing results, and session completion are never inferred from general prose or model names.

## Tool and command relationships

`TranscriptSession.Tools` groups non-duplicate, non-mirror call/result events by explicit correlation key. It works when results appear before calls. Missing calls/results remain visible. Multiple calls sharing a key are marked ambiguous; the Core does not arbitrarily choose one. Results lacking a key remain ordinary events and are counted as unassociated.

Markdown lacks native call IDs. Its input/output/error blocks receive a deterministic structural key, while `CallId` remains null. This means “same exported tool block,” not a recovered native ID.

`ActivityLayer` separates model-facing requests, runtime execution observations, and exported views. A Codex `exec` request can run many underlying commands. A runtime MCP record can overlap a model-facing call. `ToolUsageByLayer` exposes these distinctions; summing layers is a count of observations, not proof of that many independent executions.

Command requests are recognized only for known shell tool shapes. Explicit command execution records and numeric result exit codes are normalized. A few source-specific exit-status wrappers are recognized; arbitrary output saying “success” or “error” is not treated as a process status. A successful tool response can still have an unknown process exit code. Background jobs are not joined to later polling calls without an explicit native call relationship.

File paths remain source strings, including foreign operating-system paths. Requested read/write/edit/search/patch targets are separate from reported changes, backups, or delivered files. A file listed in a request or backup is not proof of a successful write. Shell command text is not statically evaluated to guess files touched.

## Queries

```csharp
var query = new EventQuery
{
    Kinds = new HashSet<EventKind> { EventKind.ToolResult, EventKind.Error },
    IsError = true,
    Text = "build"
};

await foreach (var item in query.ApplyAsync(reader.ReadEventsAsync(input)))
{
    // Process results and stop early with break if desired.
}
```

Text search is literal and ordinal case-insensitive across normalized text, commands, tool names, and file paths. Enable `SearchNativeData` to search otherwise unnormalized native fields. Role/tool/model/provider/file equality filters are exact and case-sensitive; they do not apply the current machine's filesystem rules to a foreign path. Time filtering uses `[From, Until)` and excludes untimed events. Duplicate replays are excluded by default; mirrors are searchable by default.

## Facts and usage accounting

| Fact | Definition |
|---|---|
| `NativeRecords` | Distinct input record indices seen, including malformed records and terminal input diagnostics |
| `EventCounts` | Normalized counts excluding exact duplicate replays and designated mirrors |
| `ObservedDuration` | Range of known event timestamps when at least two timestamped observations exist; not active work time |
| `TurnCount` | Explicit turn-start observations; null when unavailable |
| User/assistant messages | Distinct native message IDs, or record identities when IDs are absent; tool-result messages do not become user turns |
| Errors | Explicit error events, failed tool results, or nonzero normalized command exits |
| Compactions | Primary compaction records, excluding known alternate notifications |
| Repeated tools | Same tool name and canonicalized JSON arguments (or exact non-JSON argument text); factual repetition, not a judgment |
| Commands | Separate `execution-events` and `shell-tools` layers; exit-code successes/failures and unknown exits |
| Files | Distinct requested paths and reported activity paths, separately |

Usage is a list of **separate accounting series**. Do not add series together:

- Response/step series sum one latest sample per native response/step key.
- Session-cumulative series keep the latest known native snapshot, not the sum of snapshots.
- Claude's repeated response usage blocks share `message.id`, preventing repeated billing counts.
- OpenCode step-finish usage is a mirror when the same message exposes response totals.
- Codex response, thread-total, and token-count series overlap and remain separate.
- Native costs remain separate from token totals. No prices are looked up or calculated.

Every token field is nullable. Cache and reasoning counters can overlap other counters, and semantics differ between agents. `Total` is never manufactured by adding fields. `KnownCounterSamples` shows how many samples supplied each field. Context maxima use explicit observations. Cumulative counter regressions are counted and the latest value remains visible; the Core does not silently “repair” resets by taking a maximum.

## Large files, partial data, and limits

JSONL is read record by record. OpenCode JSON is read one root property/message at a time. Markdown is read one section at a time. ZIP and Zstandard are decoded as streams. Default limits are 32 Mi characters per native record, 2 Gi decoded characters per input, and 1,000,000 events for a full load. Limits are configurable with `ReadOptions`.

Streaming does not retain message bodies, but duplicate detection, tool relationships, distinct files, and response usage keep identifier-sized state proportional to observed records/keys. It is not constant-memory indexing. Full loads retain event and native text data and sort it in memory; use streaming for large inputs.

Files are opened read-only with sharing for active writers. Plain files and compressed streams are bounded to the byte length observed on open. This gives a finite read of a growing log; it is not an atomic filesystem snapshot if an external process rewrites existing bytes. ZIP metadata is likewise read without locking out its owner. Cancellation propagates as `OperationCanceledException`.

Malformed JSONL records produce diagnostics containing the malformed text and allow later lines to load. Truncated JSON exports keep complete preceding messages and the available malformed tail. Broken outer JSON structure cannot always be resynchronized. A damaged compressed tail reports a diagnostic and keeps records already emitted; bytes the decoder cannot recover remain only in the original source. Record/decompression limits stop reading with a diagnostic instead of silently truncating an event. Unreadable files, unrecognized headers, or ambiguous ZIP inputs fail explicitly. An explicit format helps with headerless partial files.

The implementation deliberately has no persistence/index migration layer. A later local index should store input revisions and normalization version, preserve these native locations, and invalidate changed prefixes rather than merging uncertain histories.
