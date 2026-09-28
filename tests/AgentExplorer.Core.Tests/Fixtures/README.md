# Sanitized fixtures

These small transcripts were authored from the shapes observed in real local files. They contain invented session IDs, messages, models, paths, code and opaque payloads; they are not copied private conversations.

- `codex.jsonl`: metadata, messages and alternate views, reasoning, calls/results, an execution event, file changes, token series, compaction, a missing result, an error, an unknown type, exact duplicate replay, and out-of-order timestamps.
- `claude.jsonl`: repeated usage on blocks of one response, thinking/signature, multiple tools/results, a failed tool, compaction boundary, file backup, API error, titles and costs.
- `opencode-v1.json`: canonical V1 envelope with message info/parts, step events, usage, patch, error and unknown fields.
- `opencode-v2.json` and `.md`: the same conceptual conversation in both V2 export representations, intentionally preserving their different levels of information.
- `dsh.jsonl`: storage v3 header, parent identity, request/context metadata, embedded and durable tool calls, source-event references, stream data, usage, repeated tools, failure and boundaries.
- `dsh-bundle.zip`: the DSH fixture as a root member, a second sanitized child member, and an inert example attachment. It mirrors the real multi-session export layout.
- `session.v3.jsonl.zstd`: the DSH fixture encoded as five concatenated independent Zstandard frames.

Tests create additional temporary malformed, truncated, ambiguous, Unicode, and large inputs. The damaged-frame test compresses and truncates actual bytes. Filesystem discovery tests use real directories; no mocked parser, storage, or correlation behavior is substituted.
