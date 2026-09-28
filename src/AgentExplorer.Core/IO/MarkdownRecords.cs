using System.Runtime.CompilerServices;
using System.Text;

using AgentExplorer.Core.Models;
using AgentExplorer.Core.Parsing;

namespace AgentExplorer.Core.IO;

internal static class MarkdownRecords
{
    internal static async IAsyncEnumerable<NativeRecord> ReadAsync(InputStream input, ReadOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        long recordIndex = 0;
        long startLine = 1;
        var fence = new MarkdownFence();
        await foreach (var line in NativeRecords.ReadAsync(input, TranscriptFormat.OpenCodeMarkdown, options, cancellationToken).ConfigureAwait(false))
        {
            var text = line.Text.TrimEnd('\r', '\n');
            var outside = !fence.IsOpen;
            fence.Observe(text);
            if (outside && OpenCodeMarkdownParser.IsHeading(text) && builder.Length > 0)
            {
                yield return new NativeRecord(new SourceLocation(input.Path, input.Entry, recordIndex++, startLine), builder.ToString());
                builder.Clear();
                startLine = line.Location.LineNumber ?? 1;
            }
            if (builder.Length + line.Text.Length > options.MaxRecordCharacters)
            {
                throw new InvalidDataException("Markdown section character limit exceeded.");
            }

            builder.Append(line.Text);
        }
        if (builder.Length > 0)
        {
            yield return new NativeRecord(new SourceLocation(input.Path, input.Entry, recordIndex, startLine), builder.ToString());
        }
    }
}
