using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using AgentExplorer.Core.IO;
using AgentExplorer.Core.Models;
using AgentExplorer.Core.Parsing;

namespace AgentExplorer.Core;

/// <summary>Read-only entry point. Instances contain no session state and can be shared between consumers.</summary>
public sealed class TranscriptReader
{
    /// <summary>Increment when normalization changes so persisted events can be rebuilt.</summary>
    public const int NormalizationVersion = 1;

    /// <summary>Identifies content, including DSH ZIP members and Zstandard streams.</summary>
    public async Task<TranscriptFormat> DetectFormatAsync(TranscriptInput input, CancellationToken cancellationToken = default)
    {
        if (input.Format is { } supplied)
        {
            return supplied;
        }

        using var source = new InputStream(input);
        using var cursor = new TextCursor(source.Stream, new ReadOptions { MaxDecodedCharacters = 128 * 1024 });
        var prefix = new StringBuilder();
        var line = new StringBuilder();
        for (var count = 0; count < 128 * 1024; count++)
        {
            var character = await cursor.TakeAsync(cancellationToken).ConfigureAwait(false);
            if (character >= 0)
            {
                prefix.Append((char)character);
                line.Append((char)character);
            }
            if (character is '\n' or -1)
            {
                var detected = DetectRecord(line.ToString());
                if (detected is not null)
                {
                    return detected.Value;
                }

                line.Clear();
            }
            if (character is '\n' or -1 || count % 256 == 255)
            {
                var text = prefix.ToString().TrimStart();
                if (text.StartsWith("# ", StringComparison.Ordinal) && text.Contains("**Session ID:**", StringComparison.Ordinal))
                {
                    return TranscriptFormat.OpenCodeMarkdown;
                }
                // Recognize the root envelope before reading a potentially enormous messages array.
                if (text.StartsWith('{'))
                {
                    var firstProperty = text[1..].TrimStart();
                    if (firstProperty.StartsWith("\"info\"", StringComparison.Ordinal) || firstProperty.StartsWith("\"messages\"", StringComparison.Ordinal))
                    {
                        return TranscriptFormat.OpenCodeJson;
                    }
                }
            }
            if (character < 0)
            {
                break;
            }
        }
        throw new InvalidDataException("Unrecognized transcript. Supply a format for a headerless or malformed partial file.");
    }

    private static TranscriptFormat? DetectRecord(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = root.Str("type");
            if (type is "session_meta" or "event_msg" or "response_item" or "turn_context" or "world_state" or "token_usage_record" or "compacted")
            {
                return TranscriptFormat.CodexJsonl;
            }

            if (type == "session" && root.Get("version").ValueKind == JsonValueKind.Number || root.Get("seq").ValueKind == JsonValueKind.Number && root.Get("data").ValueKind == JsonValueKind.Object)
            {
                return TranscriptFormat.DshJsonl;
            }

            if (root.Str("sessionId") is not null || root.Str("uuid") is not null && type is not null)
            {
                return TranscriptFormat.ClaudeCodeJsonl;
            }

            if (root.Get("info").ValueKind == JsonValueKind.Object && root.Get("messages").ValueKind == JsonValueKind.Array)
            {
                return TranscriptFormat.OpenCodeJson;
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>Streams events in native record order with bounded record buffers. It never executes transcript tools.</summary>
    public async IAsyncEnumerable<TranscriptEvent> ReadEventsAsync(TranscriptInput input, ReadOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new ReadOptions();
        if (options.MaxRecordCharacters <= 0 || options.MaxDecodedCharacters <= 0 || options.MaxLoadedEvents <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        var format = await DetectFormatAsync(input, cancellationToken).ConfigureAwait(false);
        using var source = new InputStream(input);
        var context = new ParsingContext(format);
        var records = format == TranscriptFormat.OpenCodeMarkdown ? MarkdownRecords.ReadAsync(source, options, cancellationToken) : NativeRecords.ReadAsync(source, format, options, cancellationToken);
        await using var iterator = records.GetAsyncEnumerator(cancellationToken);
        long sequence = 0;
        long lastRecord = -1;
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exception? failure = null;
            var moved = false;
            try { moved = await iterator.MoveNextAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or DecoderFallbackException || ex.GetType().Namespace == "ZstdSharp") { failure = ex; }
            if (failure is not null)
            {
                var native = new NativeRecord(new SourceLocation(source.Path, source.Entry, lastRecord + 1), "");
                yield return context.Event(native, EventKind.Diagnostic, "input.error") with
                {
                    Id = $"r{lastRecord + 1}:error",
                    Sequence = sequence,
                    Diagnostic = new TranscriptDiagnostic("input-error", failure.Message)
                };
                yield break;
            }
            if (!moved)
            {
                yield break;
            }

            var record = iterator.Current;
            lastRecord = record.Location.RecordIndex;
            List<TranscriptEvent> events;
            string? duplicateOf = null;
            try
            {
                if (format == TranscriptFormat.OpenCodeMarkdown)
                {
                    events = [.. OpenCodeMarkdownParser.Parse(record, context)];
                }
                else if (string.IsNullOrWhiteSpace(record.Text))
                {
                    events = [context.Event(record, EventKind.Unknown, "blank") with { IsMirror = true }];
                }
                else
                {
                    using var document = JsonDocument.Parse(record.Text);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object && format != TranscriptFormat.OpenCodeJson)
                    {
                        throw new JsonException("Expected a native record object.");
                    }

                    var identity = Identity(root, record, format);
                    if (identity is not null)
                    {
                        var key = identity + ":" + record.ComputeSha256();
                        if (seen.TryGetValue(key, out var first))
                        {
                            duplicateOf = first;
                        }
                        else
                        {
                            seen.Add(key, $"r{record.Location.RecordIndex}");
                        }
                    }
                    events = [.. Parse(record, root, duplicateOf is null ? context : context.Copy())];
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
            {
                events = [context.Event(record, EventKind.Diagnostic, "record.error") with { Diagnostic = new TranscriptDiagnostic("malformed-record", ex.Message) }];
            }
            if (events.Count == 0)
            {
                events.Add(context.Event(record, EventKind.Unknown, "empty"));
            }

            for (var i = 0; i < events.Count; i++)
            {
                yield return events[i] with { Id = $"r{record.Location.RecordIndex}:{i}", Sequence = sequence++, DuplicateOf = duplicateOf is null ? null : $"{duplicateOf}:{i}" };
            }
        }
    }

    /// <summary>Loads an individual session and constructs chronological views and tool relationships.</summary>
    public async Task<TranscriptSession> LoadAsync(TranscriptInput input, ReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ReadOptions();
        var events = new List<TranscriptEvent>();
        await foreach (var item in ReadEventsAsync(input, options, cancellationToken).ConfigureAwait(false))
        {
            if (events.Count >= options.MaxLoadedEvents)
            {
                throw new InvalidDataException("Loaded event limit exceeded. Use ReadEventsAsync or raise MaxLoadedEvents.");
            }

            events.Add(item);
        }
        return new TranscriptSession(input, events, options.OrderByTimestamp);
    }

    private static IEnumerable<TranscriptEvent> Parse(NativeRecord record, JsonElement root, ParsingContext context) => context.Format switch
    {
        TranscriptFormat.CodexJsonl => CodexParser.Parse(record, root, context),
        TranscriptFormat.ClaudeCodeJsonl => ClaudeCodeParser.Parse(record, root, context),
        TranscriptFormat.DshJsonl => DshParser.Parse(record, root, context),
        TranscriptFormat.OpenCodeJson => OpenCodeParser.Parse(record, root, context),
        _ => throw new NotSupportedException()
    };

    private static string? Identity(JsonElement root, NativeRecord record, TranscriptFormat format) => format switch
    {
        TranscriptFormat.CodexJsonl => root.Str("ordinal") ?? root.Get("payload").Str("id"),
        TranscriptFormat.ClaudeCodeJsonl => root.Str("uuid"),
        TranscriptFormat.DshJsonl => root.Str("seq"),
        TranscriptFormat.OpenCodeJson => record.Location.JsonPointer?.StartsWith("/messages/", StringComparison.Ordinal) == true ? root.Str("id") ?? root.Get("info").Str("id") : null,
        _ => null
    };
}
