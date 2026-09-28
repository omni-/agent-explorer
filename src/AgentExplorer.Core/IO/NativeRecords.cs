using System.Runtime.CompilerServices;
using System.Text.Json;

using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.IO;

internal static class NativeRecords
{
    internal static async IAsyncEnumerable<NativeRecord> ReadAsync(InputStream input, TranscriptFormat format,
        ReadOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cursor = new TextCursor(input.Stream, options);
        long index = 0;
        if (format != TranscriptFormat.OpenCodeJson)
        {
            while (true)
            {
                var line = cursor.Line;
                var text = await cursor.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (text is null)
                {
                    yield break;
                }

                yield return new NativeRecord(new SourceLocation(input.Path, input.Entry, index++, line), text);
            }
        }

        await cursor.SkipWhitespaceAsync(cancellationToken).ConfigureAwait(false);
        if (await cursor.TakeAsync(cancellationToken).ConfigureAwait(false) != '{')
        {
            throw new InvalidDataException("Expected an OpenCode JSON export object.");
        }

        while (true)
        {
            await cursor.SkipWhitespaceAsync(cancellationToken).ConfigureAwait(false);
            if (await cursor.PeekAsync(cancellationToken).ConfigureAwait(false) == '}')
            {
                await cursor.TakeAsync(cancellationToken).ConfigureAwait(false);
                await cursor.SkipWhitespaceAsync(cancellationToken).ConfigureAwait(false);
                if (await cursor.PeekAsync(cancellationToken).ConfigureAwait(false) >= 0)
                {
                    throw new InvalidDataException("Unexpected trailing export data.");
                }

                yield break;
            }
            var keyText = await cursor.ReadJsonValueAsync(cancellationToken).ConfigureAwait(false);
            var key = JsonSerializer.Deserialize<string>(keyText) ?? throw new InvalidDataException("Missing export property name.");
            await cursor.SkipWhitespaceAsync(cancellationToken).ConfigureAwait(false);
            if (await cursor.TakeAsync(cancellationToken).ConfigureAwait(false) != ':')
            {
                throw new InvalidDataException("Missing export property separator.");
            }

            await cursor.SkipWhitespaceAsync(cancellationToken).ConfigureAwait(false);
            if (key == "messages" && await cursor.PeekAsync(cancellationToken).ConfigureAwait(false) == '[')
            {
                await cursor.TakeAsync(cancellationToken).ConfigureAwait(false);
                var message = 0;
                while (true)
                {
                    await cursor.SkipWhitespaceAsync(cancellationToken).ConfigureAwait(false);
                    if (await cursor.PeekAsync(cancellationToken).ConfigureAwait(false) == ']')
                    {
                        await cursor.TakeAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    var line = cursor.Line;
                    var text = await cursor.ReadJsonValueAsync(cancellationToken).ConfigureAwait(false);
                    if (text.Length == 0)
                    {
                        throw new InvalidDataException("Truncated messages array.");
                    }

                    yield return new NativeRecord(new SourceLocation(input.Path, input.Entry, index++, line, $"/messages/{message++}"), text);
                    await cursor.SkipWhitespaceAsync(cancellationToken).ConfigureAwait(false);
                    if (await cursor.PeekAsync(cancellationToken).ConfigureAwait(false) == ']')
                    {
                        continue;
                    }

                    if (await cursor.TakeAsync(cancellationToken).ConfigureAwait(false) != ',')
                    {
                        throw new InvalidDataException("Truncated or malformed messages array.");
                    }
                }
            }
            else
            {
                var line = cursor.Line;
                var text = await cursor.ReadJsonValueAsync(cancellationToken).ConfigureAwait(false);
                yield return new NativeRecord(new SourceLocation(input.Path, input.Entry, index++, line, "/" + Parsing.JsonValue.PointerSegment(key)), text);
            }
            await cursor.SkipWhitespaceAsync(cancellationToken).ConfigureAwait(false);
            if (await cursor.PeekAsync(cancellationToken).ConfigureAwait(false) == '}')
            {
                continue;
            }

            if (await cursor.TakeAsync(cancellationToken).ConfigureAwait(false) != ',')
            {
                throw new InvalidDataException("Truncated or malformed export object.");
            }
        }
    }
}
