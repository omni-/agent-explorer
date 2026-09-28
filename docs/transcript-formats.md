# Transcript formats encountered

This describes the files actually inspected on the development machine. It is not a claim that every historical or future release has the same schema. The [verification account](task-notes/2026-09-28-core-verification.md) separates inspected versions from fully exercised inputs.

## Codex rollouts

Native input is UTF JSONL under the Codex data directory's `sessions` and `archived_sessions` directories. Observed records include `session_meta`, `turn_context`, `world_state`, `response_item`, `event_msg`, `token_usage_record`, `compacted`, and occasional inter-agent metadata.

`session_meta.payload` exposes ID, creation time, cwd, CLI version, source/originator, model provider, Git metadata, instructions, roots, and context-window identity. `turn_context` and settings events carry model, cwd, approvals, sandbox settings, effort, and turn identity. Only useful common fields are lifted into `SessionMetadata`; the entire source record remains attached.

`response_item` carries messages, reasoning, function/custom calls, and outputs. `call_id` connects calls/results. `event_msg.item_completed` can expose lower-level `CommandExecution`, `FileChange`, `McpToolCall`, and other execution observations even when the model's outer call was a Code Mode script. Command arrays, cwd, process ID, outputs and numeric exits are normalized. Exact patch bodies, parsed commands, tool metadata and execution timestamps stay native.

Message/reasoning projections, completion notifications and compaction notifications can repeat facts present in primary records. These alternate views are marked `IsMirror` and remain searchable. They are excluded from default counts, so counts describe the chosen primary views rather than every UI notification. A transcript containing only alternate views can consequently have fewer primary message/compaction counts; the original events are still available.

Reasoning can include cleartext summaries, empty summaries, or encrypted content. The Core does not decrypt or invent hidden reasoning. `compacted` replacement/guardian histories are preserved as native snapshots rather than replayed as new conversation messages. Context-window capacity is different from consumed context; the latter is not guessed from lifetime token totals.

Modern `token_usage_record` has per-response, per-turn, and per-thread counters. The Core exposes response and thread accounting series and preserves all other counters. Older `event_msg.token_count` snapshots form their own series. Rate limits and spend-control metadata remain native and are not treated as monetary cost.

## Claude Code project JSONL

Native records live under `projects/<encoded-project>/*.jsonl`, with additional nested transcripts possible. They expose `sessionId`, `uuid`, `parentUuid`, timestamps, cwd, version, branch, and sometimes sidechain/agent information. The Core preserves those relationships and additional metadata even when it has no common normalized field for them.

User and assistant `message.content` can be a string or a list of blocks. The observed blocks include text, images, thinking with signatures, `tool_use`, and `tool_result`. A result often appears in a record whose message role is `user`; it is normalized as a tool result rather than counted as a human prompt. `tool_use_id` links it to its call. `toolUseResult` can carry outputs, interruption flags, or tool-specific data beyond the displayed result.

Several transcript records can represent blocks of one assistant response and repeat that response's usage. The response's `message.id` is the accounting key; `uuid` still identifies the native record. Cache read/write counters are separate native fields. A `cost-state` record can expose session USD cost and per-model totals. These are separate accounting series, not additions to response totals.

Observed extra records include attachments, titles, queue operations, file-history snapshots/deltas, system API errors and hook summaries. Backups are normalized as reported file activity, not successful writes. Thinking text in the supplied modern transcript was often empty despite a signature and thinking-token usage. The text cannot be reconstructed from those fields. Provider identity is not inferred from a Claude model name.

Compaction boundary/summary decoding is covered by authored fixtures. The inspected live Claude samples did not establish every compaction variant. Explicit turn starts were unavailable in those samples; `TurnCount` remains null and message counts are available separately.

## OpenCode V1 JSON

The canonical envelope is:

```json
{"info":{"id":"session-id","version":"1.x"},"messages":[{"info":{"id":"message-id","role":"assistant"},"parts":[]}]}
```

Real retained V1 rows were available in the local SQLite database, with session versions 1.18.18 through 1.18.31. Verification reconstructed this envelope from read-only session/message/part rows without launching a runtime that might migrate the database. This follows the shape used by the [V1 export command](https://github.com/anomalyco/opencode/blob/v1.18.31/packages/opencode/src/cli/cmd/export.ts). The Core itself has no SQLite or agent-runtime dependency.

Observed parts include text, reasoning, tools, step start/finish, patches, compactions and file attachments. Tool state contains `callID`, tool name, input, status, output/error, metadata, and start/end time. Numeric process exit status is commonly in metadata. Message-level model/provider, usage, cost and error data are preserved. Step-finish usage is kept as an alternate accounting view when response totals exist.

## OpenCode V2 structured JSON

The supplied V2 export keeps `info` and `messages` but messages are directly typed objects. User/system content can be `text`; assistant content is an array of text/reasoning/tool entries. Model is an object, the workspace is under `info.location.directory`, and times use `created`/`completed`.

Tool entries have an ID/name and state containing input, completed/error status, content blocks and metadata. Provider reasoning details can occur inside content state. Session and message cost/token totals both exist and are exposed separately. They are not added together.

The installed local V2 CLI package is 2.0.18. The supplied export does not record the producing application's version; the Core leaves that version unknown. The export supplies the common forensic information required here without coupling the library to the V2 event-store implementation. It may still represent a projected history rather than every internal retry or state mutation; no claim of full event-store equivalence is made.

## OpenCode Markdown `/export`

The actual format has a title, session-ID/created/updated headers, role headings such as `## Assistant (Build · model · 2.7s)`, `_Thinking:_` paragraphs, and tool input/output/error code fences. The parser respects matching code fences so quoted headings inside code remain content. JSON input is decoded when valid; malformed or truncated blocks remain native and produce diagnostics. An explicit `**Error:**` is a failed tool result.

The export lacks native message/call IDs, per-event absolute timestamps, numeric usage/cost, structured process exits, provider identity, and much of the structured metadata. Model display labels and elapsed header text are preserved; a display label is not asserted to be a provider model ID. The localized header dates have no timezone, so they remain native text.

The two supplied OpenCode exports share a session ID but have different coverage: structured JSON has **395 tool calls**, while Markdown has **95**, including two explicit tool errors. The Markdown is therefore a useful independent input but not a substitute for the JSON history. The Core does not guess why the exporter omitted earlier content or reconstruct it.

Like the exporter itself, this format cannot unambiguously escape every possible body that imitates its role/tool delimiters. Valid code fences reduce ambiguity; exact section text remains inspectable. The default discovery preference for JSON avoids silently selecting this lower-fidelity view when both are available.

## DSH v3 persistence and ZIP exports

The local persisted files are `~/.dsh/sessions/<workspace>/<session>/session.v3.jsonl.zstd`. Decoding them produces a header with `type: session`, `version: 3`, ID, creation time, cwd, and optional parent/delegation metadata, followed by records with `type`, `seq`, `time`, `data`, and optional `surfaceOp`/`sourceEventSeqs`.

The Core treats the **v3 event log as canonical**, whether supplied as raw JSONL, a compressed persisted file, or a member of a ZIP export. Compressed files contain concatenated independent frames, not one compressed JSON document. This matches the observed files and DSH's [persistence description](https://github.com/deepseek-ai/deepseek-harness/tree/master/packages/session/session-persistence-jsonl) and [frame implementation](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/session/session-persistence-jsonl/src/zstd.ts). The separate `storages/session_projcache` files are metadata caches, not complete transcripts.

One supplied ZIP contains `session.v3.jsonl` plus **13 subagent logs** under `subagents/<id>/session.v3.jsonl`. Discovery exposes each session; explicit `parentSession` values preserve the tree. Archive names need not match the internal session ID. The earlier standalone JSONL belongs to a different session and is also verified.

DSH exposes durable turn/step boundaries, request headers/configuration, model/provider, context capacity, messages, reasoning, tool calls/results, usage, stream chunks, attempts, approvals, goals, todo changes, subagent metadata and presented files. Assistant messages can embed tool-call requests; the separate `tool/call` records identify execution starts. Embedded requests are marked as alternate views to avoid doubling executed-call counts. `tool/result` wraps a user-role message with tool-result content and explicit call IDs. Harness `command/run` records are slash commands, not shell executions.

Streams, source-event references, subagent catalogs, request tool schemas, retries and other source-specific fields remain in native records. `sourceEventSeqs` is not rewritten or expanded. No context resumption, crash repair, schema migration or source mutation is performed. A missing turn end remains missing.

The local container recipe pins DSH 0.1.5-rc.1, but session headers identify storage version 3 rather than the exact producing application release. That recipe is not proof of the release that wrote each file. Upstream now describes later generations too; this implementation deliberately claims v3 only. Other versions generate an unsupported-version diagnostic and retain their native records without applying v3 meanings.

## Shared limits

Normalization is an extensible view over native data, not a complete reimplementation of each runtime. Unknown tools and fields remain recoverable. The Core does not parse arbitrary embedded JavaScript or shell code, fetch external attachments, decrypt reasoning, infer edits from commands, reconstruct absent results, infer provider identity, or price tokens. Dates, counters, call IDs and statuses that are absent remain absent.
