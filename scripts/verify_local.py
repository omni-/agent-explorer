"""Optional read-only cross-check of real transcripts against the diagnostic CLI.

Uses Python's independent JSON/ZIP/Zstandard readers. Python 3.14 is needed only
when auditing .zstd. Nothing in the Core depends on Python or agent installations.
Reports contain counts, formats, versions and hashes, never conversation bodies.
"""

import argparse
import collections
import hashlib
import json
import pathlib
import re
import sqlite3
import subprocess
import time
import zipfile


def sha(data):
    return hashlib.sha256(data).hexdigest()


def text_for(path, entry):
    if entry:
        with zipfile.ZipFile(path) as archive:
            data = archive.read(entry)
    else:
        data = path.read_bytes()
    if (entry or path.name).endswith((".zstd", ".zst")):
        import compression.zstd
        # decompress() accepts the concatenated frames written by DSH.
        data = compression.zstd.decompress(data)
    return data.decode("utf-8-sig")


def export_records(text):
    """Extract exact native value slices with Python's JSON decoder."""
    decoder = json.JSONDecoder()
    pos = 0

    def whitespace():
        nonlocal pos
        while pos < len(text) and text[pos].isspace():
            pos += 1

    whitespace()
    assert text[pos] == "{"
    pos += 1
    while True:
        whitespace()
        if text[pos] == "}":
            return
        key, pos = decoder.raw_decode(text, pos)
        whitespace()
        assert text[pos] == ":"
        pos += 1
        whitespace()
        if key == "messages":
            assert text[pos] == "["
            pos += 1
            while True:
                whitespace()
                if text[pos] == "]":
                    pos += 1
                    break
                start = pos
                _, pos = decoder.raw_decode(text, pos)
                yield text[start:pos]
                whitespace()
                if text[pos] == ",":
                    pos += 1
        else:
            start = pos
            _, pos = decoder.raw_decode(text, pos)
            yield text[start:pos]
        whitespace()
        if text[pos] == ",":
            pos += 1


def native_facts(text, format_name):
    tools = collections.Counter()
    usage = {}
    versions = set()
    if format_name == "OpenCodeMarkdown":
        for name in re.findall(r"^\*\*Tool: (.+)\*\*$", text, re.MULTILINE):
            tools[name] += 1
        return None, sha(text.encode()), tools, usage, ["markdown-export"]
    if format_name == "OpenCodeJson":
        root = json.loads(text)
        pieces = list(export_records(text))
        version = root.get("info", {}).get("version")
        if version:
            versions.add(version)
        for message in root.get("messages", []):
            info = message.get("info", message)
            for part in message.get("parts", message.get("content", [])):
                if part.get("type") == "tool":
                    tools[part.get("tool", part.get("name"))] += 1
            if "tokens" in info:
                usage[info.get("id")] = info["tokens"]
        totals = {"opencode.response": sum_counters(usage.values(), "input", "output")}
        if "tokens" in root.get("info", {}):
            totals["opencode.session"] = sum_counters([root["info"]["tokens"]], "input", "output")
        return len(pieces), sha("".join(pieces).encode()), tools, totals, sorted(versions)
    records = [json.loads(line) for line in text.splitlines() if line.strip()]
    seen = set()
    last_total = None
    for row in records:
        if format_name == "CodexJsonl":
            p = row.get("payload", {})
            identity = row.get("ordinal", p.get("id"))
        elif format_name == "ClaudeCodeJsonl":
            identity = row.get("uuid")
        else:
            identity = row.get("seq")
        fingerprint = (str(identity), json.dumps(row, sort_keys=True))
        if identity is not None and fingerprint in seen:
            continue
        seen.add(fingerprint)
        kind = row.get("type")
        if format_name == "CodexJsonl":
            p = row.get("payload", {})
            if kind == "session_meta" and p.get("cli_version"):
                versions.add(p["cli_version"])
            if kind == "response_item" and p.get("type") in ("function_call", "custom_tool_call"):
                name = p.get("name")
                if p.get("namespace"):
                    name = p["namespace"] + "." + name
                tools[name] += 1
            if kind == "event_msg" and p.get("type") == "item_completed" and p.get("item", {}).get("type") == "McpToolCall":
                item = p["item"]
                tools[item.get("server", "") + "." + item.get("tool", "")] += 1
            if kind == "token_usage_record":
                usage[p.get("response_id")] = p.get("usage", {})
                last_total = p.get("thread_token_usage", last_total)
        elif format_name == "ClaudeCodeJsonl":
            if row.get("version"):
                versions.add(row["version"])
            message = row.get("message", {})
            content = message.get("content", [])
            for block in content if isinstance(content, list) else []:
                if block.get("type") in ("tool_use", "server_tool_use"):
                    tools[block.get("name")] += 1
            if "usage" in message:
                usage[message.get("id", row.get("requestId"))] = message["usage"]
        else:
            data = row.get("data", {})
            if kind == "session":
                versions.add("storage-v" + str(row.get("version")))
            if kind == "tool/call":
                tools[data.get("name")] += 1
            if kind == "assistant/message" and "usage" in data:
                usage[data.get("message", {}).get("id", row.get("seq"))] = data["usage"]
    if format_name == "CodexJsonl":
        totals = {"codex.response": sum_counters(usage.values(), "input_tokens", "output_tokens")}
        if last_total:
            totals["codex.thread"] = sum_counters([last_total], "input_tokens", "output_tokens")
    elif format_name == "ClaudeCodeJsonl":
        totals = {"claude.response": sum_counters(usage.values(), "input_tokens", "output_tokens")}
    else:
        totals = {"dsh.response": sum_counters(usage.values(), "inputTokens", "outputTokens")}
    return len(text.splitlines()), sha(text.encode()), tools, totals, sorted(versions)


def sum_counters(samples, input_name, output_name):
    samples = list(samples)
    def total(name):
        known = [x[name] for x in samples if x.get(name) is not None]
        return sum(known) if known else None
    return {"Input": total(input_name), "Output": total(output_name)}


def v1_snapshots(database, output):
    """Read V1 rows without invoking a runtime that could migrate the database.

    The envelope follows the V1 export command: {info, messages:[{info,parts}]}.
    These are verification snapshots, explicitly not claimed as CLI exports.
    """
    connection = sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    rows = list(connection.execute("select session.id,version,count(message.id) as n from session join message on message.session_id=session.id group by session.id order by n desc"))
    selected = []
    for version in ("1.18.18", "1.18.21", "1.18.31"):
        selected += [row for row in rows if row["version"] == version][:1]
    output.mkdir(parents=True, exist_ok=True)
    paths = []
    for row in selected:
        session = connection.execute("select * from session where id=?", (row["id"],)).fetchone()
        info = {"id": session["id"], "version": session["version"], "projectID": session["project_id"], "directory": session["directory"], "title": session["title"], "time": {"created": session["time_created"], "updated": session["time_updated"]}}
        if session["parent_id"]:
            info["parentID"] = session["parent_id"]
        messages = []
        for row_message in connection.execute("select * from message where session_id=? order by time_created,id", (session["id"],)):
            message = json.loads(row_message["data"])
            message.update(id=row_message["id"], sessionID=session["id"])
            parts = []
            for row_part in connection.execute("select * from part where message_id=? order by time_created,id", (row_message["id"],)):
                part = json.loads(row_part["data"])
                part.update(id=row_part["id"], sessionID=session["id"], messageID=row_message["id"])
                parts.append(part)
            messages.append({"info": message, "parts": parts})
        target = output / ("opencode-v1-" + session["version"] + ".json")
        target.write_text(json.dumps({"info": info, "messages": messages}, ensure_ascii=True), encoding="utf-8")
        paths.append(target)
    connection.close()
    return paths


def representative(home, pattern, version_reader):
    by_version = {}
    files = list(home.rglob(pattern)) if home.exists() else []
    for path in files:
        if path.stat().st_mtime > time.time() - 120:
            continue
        try:
            with path.open(encoding="utf-8-sig") as handle:
                for _, line in zip(range(20), handle):
                    version = version_reader(json.loads(line))
                    if version:
                        by_version.setdefault(version, path)
                        break
        except (ValueError, OSError):
            continue
    # Cover oldest, middle, latest observed versions plus the largest completed log.
    versions = sorted(by_version)
    chosen = [by_version[versions[i]] for i in {0, len(versions) // 2, len(versions) - 1}] if versions else []
    stable = [p for p in files if p.stat().st_mtime <= time.time() - 120]
    if stable:
        chosen.append(max(stable, key=lambda p: p.stat().st_size))
    return sorted(set(chosen))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data-root", type=pathlib.Path, default=pathlib.Path("data"))
    parser.add_argument("--user-sessions", action="store_true")
    parser.add_argument("--opencode-v1-db", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path, default=pathlib.Path(".local/live-verification.json"))
    args = parser.parse_args()
    executable = pathlib.Path("src/AgentExplorer.Cli/bin/Debug/net10.0/AgentExplorer.Cli" + (".exe" if __import__("os").name == "nt" else "")).resolve()
    paths = [p for p in args.data_root.rglob("*") if p.is_file() and p.suffix in (".json", ".jsonl", ".md", ".zip", ".zstd")]
    if args.user_sessions:
        home = pathlib.Path.home()
        paths += representative(home / ".codex/sessions", "rollout-*.jsonl", lambda r: r.get("payload", {}).get("cli_version"))
        paths += representative(home / ".claude/projects", "*.jsonl", lambda r: r.get("version"))
        paths += list((home / ".dsh/sessions").rglob("*.zstd"))
    if args.opencode_v1_db:
        paths += v1_snapshots(args.opencode_v1_db, pathlib.Path(".local/live"))
    inputs = []
    for path in sorted(set(paths)):
        if path.suffix == ".zip":
            with zipfile.ZipFile(path) as archive:
                inputs += [(path, entry) for entry in archive.namelist() if entry.endswith(".jsonl")]
        else:
            inputs.append((path, None))
    reports = []
    for path, entry in inputs:
        before = sha(path.read_bytes())
        command = [str(executable), "verify", str(path)]
        if entry:
            command += ["DshJsonl", entry]
        result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8")
        label = path.name + ("!" + entry if entry else "")
        if result.returncode not in (0, 2):
            raise RuntimeError(f"CLI failed for {label}: {result.stderr[:200]}")
        report = json.loads(result.stdout)
        stats = report["Statistics"]
        text = text_for(path, entry)
        count, native_hash, tools, expected_usage, versions = native_facts(text, report["Format"])
        assert before == sha(path.read_bytes()), f"Source changed during audit: {label}"
        assert native_hash == report["NativeRecordsSha256"], f"Native text differs: {label}"
        if count is not None:
            assert count == stats["NativeRecords"], f"Record count differs: {label}"
        assert dict(tools) == stats["ToolUsage"], f"Tool counts differ: {label}"
        actual_usage = {x["Series"]: x for x in stats["Usage"]}
        for series, counters in expected_usage.items():
            if series not in actual_usage and all(value is None for value in counters.values()):
                continue
            for field, value in counters.items():
                assert actual_usage[series]["Tokens"][field] == value, f"{series}.{field} differs: {label}"
        assert result.returncode == 0, f"Parser warnings for {label}"
        summary = {"input": label, "format": report["Format"], "versions": versions, "sourceSha256": before,
                   "nativeRecordsSha256": native_hash, "bytes": path.stat().st_size, "nativeRecords": stats["NativeRecords"],
                   "events": stats["Events"], "tools": sum(tools.values()), "unmatchedCalls": stats["CallsWithoutResults"],
                   "unmatchedResults": stats["ResultsWithoutCalls"], "milliseconds": report["ElapsedMilliseconds"],
                   "checks": ["source unchanged", "native text preserved", "native count", "tool counts", "usage counters"]}
        reports.append(summary)
        print(json.dumps(summary), flush=True)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(reports, indent=2), encoding="utf-8")
    print(f"PASS: {len(reports)} real inputs; report: {args.output}")


if __name__ == "__main__":
    main()
