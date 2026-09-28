# Consumer API and contracts

## Entry points

- `SessionDiscovery.DiscoverAsync`: available session identities, preferred inputs, alternatives, and discovery issues.
- `TranscriptReader.DetectFormatAsync`: bounded content detection; an explicit `TranscriptInput.Format` bypasses detection.
- `TranscriptReader.ReadEventsAsync`: asynchronous events in source record order.
- `TranscriptReader.LoadAsync`: a session with metadata, chronological events, native records, tool relationships, and statistics.
- `EventQuery`: composable property filters and literal content search, for loaded or streaming events.
- `SessionStatisticsBuilder`: incremental factual aggregates without retaining conversation bodies.
- `LocalTranscriptIndex`: persistent input revisions, incremental scans, and paginated input/event queries across sessions.

`TranscriptReader` and `SessionDiscovery` contain no per-session mutable state and can be shared. Create a separate statistics builder for each input. Persistence is opt-in through `LocalTranscriptIndex` and a caller-selected database path. The Core does not own a logger; callers receive diagnostics as data and decide how to display or log them.

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

## Local index

```csharp
using AgentExplorer.Core.Indexing;

var index = new LocalTranscriptIndex(@"D:\indexes\transcripts.db");
var scan = await index.RescanAsync(new DiscoveryOptions
{
    UseDefaultLocations = false,
    ExportDirectories = [@"D:\exports"]
});
foreach (var issue in scan.Issues)
{
    Console.Error.WriteLine($"{issue.Path}: {issue.Message}");
}

var filter = new EventQuery { Text = "build", IsError = true };
var inputs = new IndexInputQuery { Source = AgentSource.Codex };
var page = index.QueryEvents(filter, inputs, pageSize: 50);
foreach (var hit in page.Items)
{
    Console.WriteLine($"{hit.SessionId} / {hit.InputId} / {hit.Revision}: {hit.Event.Text}");
}
if (page.ContinuationToken is { } token)
{
    var next = index.QueryEvents(filter, inputs, pageSize: 50, continuationToken: token);
}
```

### Scanning and revision identity

`RescanAsync` uses the existing discovery rules, indexes preferred **and alternative** inputs, and treats its discovery options as the complete desired catalog. Missing inputs are removed only when discovery has no issues. Changing the discovery scope intentionally changes the catalog. `UpdateAsync(IEnumerable<TranscriptInput>)` supports explicit formats and ZIP members, preserves other indexed inputs by default, and accepts `removeAbsentInputs: true` for an authoritative catalog. Neither API changes source files.

An input ID identifies a full file path and optional archive member. Local paths follow the host's case rules; archive member names remain case-sensitive. ZIP root shorthand resolves to its explicit member before identity is assigned. Source/native session ID is searchable metadata, so copies, alternative formats, anonymous transcripts, and unrelated sources with colliding IDs remain independent. Cross-session queries search every indexed input; they do not deduplicate same-session exports or select the preferred one.

An input revision includes SHA-256 of the source file bytes, detected or supplied format, `TranscriptReader.NormalizationVersion`, and the record/decoded-character limits. ZIP members share the outer archive's content hash but have distinct input IDs. A change anywhere in an archive invalidates its indexed members. File timestamps and lengths alone never establish equality. Maintainers must increment `NormalizationVersion` when parser behavior changes.

Unchanged inputs reuse their stored events, native records, metadata, and statistics. Scans still read source bytes for hashing, and discovery still reads a bounded metadata prefix. Changed inputs are copied to temporary files, checked against the hash, then streamed through the normal reader. Appends rebuild the affected input from its beginning: parser context, duplicate detection, metadata, and incomplete tails can affect later events. There is no tail-only parser checkpoint. Rewrites and truncations replace the previous revision completely. This prevents old events or diagnostics from surviving an edited prefix.

Copying is bounded to the length observed on open. A source that changes during copying reports an issue for retry; a concurrent append beyond that bound appears on the next scan. This is a finite captured byte sequence, not an atomic filesystem snapshot of an externally rewritten file. Temporary copies are removed after each file is handled. The original path, archive member, record index, line number, and JSON pointer remain in each indexed event.

`IndexScanResult` reports updated, unchanged, and removed input counts, the committed index revision, and issues. Input failures keep the last successfully indexed revision and report an issue. Parser diagnostics remain searchable events, just as with `TranscriptReader`; a partial but readable input can be indexed with diagnostics. Newly indexed warning/error diagnostics also appear in scan issues. Cancellation or a database failure rolls back the scan. Readers see one committed catalog, and concurrent writers are serialized by SQLite with a five-second lock timeout.

### Querying and pagination

`QueryInputs` returns `IndexedInput` entries with full indexed metadata, statistics, source byte hash, normalization version, and revision. `IndexInputQuery` filters exact input ID, source, native session ID, and workspace. The same input filters apply to `QueryEvents` alongside all existing `EventQuery` filters. Text and file comparisons use the same ordinal semantics as the streaming reader, including Unicode, literal `%`/`_` characters, and foreign paths. Queries operate entirely on the index and can run after source files are removed or become unavailable.

Input pages order by stable input ID. Event pages order by input ID and source sequence; this provides a deterministic traversal across tied or missing timestamps. This is not a global timestamp sort. Event identity is `(InputId, Revision, Event.Id)`. A record-local ID alone is insufficient across revisions or sessions.

Page sizes range from 1 to 1,000, defaulting to 100. `ContinuationToken` is null when no matching results remain. Tokens bind the query filters, query type, database revision, and last returned position. Page size can change between requests. Changing filters or supplying a malformed token throws `ArgumentException`. Any committed catalog change invalidates earlier tokens with `StaleIndexCursorException`; restart without the token. An unchanged rescan preserves tokens, including across process restarts. Previous revisions are replaced, not kept as a queryable history.

Queries use a SQLite read transaction and keyset pagination. Input, kind, property, and time filters run in SQL; literal text and file-path matching run through `EventQuery.Matches`. A text search may scan many candidates. Page bodies are bounded by page size, but individual records still follow decoding limits. Indexing streams events and does not use `MaxLoadedEvents`; factual aggregates retain identifier-sized state as described above. Native text is stored once per record, shared by its indexed events.

### Storage and versions

The constructor performs no I/O. The first scan creates the selected SQLite database; queries require an existing index. Connections are scoped to each operation. The index stores decoded native text and normalized data in the database; it uses SQLite WAL files beside it and transient source copies in the system temporary directory. No runtime network access or agent installation is needed. Query methods are synchronous because the SQLite provider executes database operations synchronously; scans use asynchronous source reads and support cancellation.

The database has a separate schema version and application ID. Unsupported or unrelated databases are rejected without migration; create a new index path and rescan. An older normalization version requires rescanning the affected inputs before queries can run. Changing decoding limits rebuilds supplied inputs on the next scan. Database recovery and historical revision retention are outside this API.
