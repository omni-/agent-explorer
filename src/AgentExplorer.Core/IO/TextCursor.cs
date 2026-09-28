using System.Text;

namespace AgentExplorer.Core.IO;

internal sealed class TextCursor(Stream stream, ReadOptions options) : IDisposable
{
    internal long Line { get; private set; } = 1;

    private readonly StreamReader _reader = new(stream, new UTF8Encoding(false, true), true, 64 * 1024, true);
    private readonly char[] _buffer = new char[16 * 1024];
    private int _position;
    private int _length;
    private long _total;

    internal async ValueTask<int> PeekAsync(CancellationToken cancellationToken)
    {
        if (_position == _length)
        {
            _length = await _reader.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            _position = 0;
        }
        return _length == 0 ? -1 : _buffer[_position];
    }

    internal async ValueTask<int> TakeAsync(CancellationToken cancellationToken)
    {
        var value = await PeekAsync(cancellationToken).ConfigureAwait(false);
        if (value < 0)
        {
            return value;
        }

        if (++_total > options.MaxDecodedCharacters)
        {
            throw new InvalidDataException("Decoded input character limit exceeded.");
        }

        _position++;
        if (value == '\n')
        {
            Line++;
        }

        return value;
    }

    internal async ValueTask SkipWhitespaceAsync(CancellationToken cancellationToken)
    {
        while (await PeekAsync(cancellationToken).ConfigureAwait(false) is var c && c >= 0 && char.IsWhiteSpace((char)c))
        {
            await TakeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        while (await TakeAsync(cancellationToken).ConfigureAwait(false) is var c && c >= 0)
        {
            Append(builder, c);
            if (c == '\n')
            {
                break;
            }
        }
        return builder.Length == 0 ? null : builder.ToString();
    }

    internal async ValueTask<string> ReadJsonValueAsync(CancellationToken cancellationToken)
    {
        await SkipWhitespaceAsync(cancellationToken).ConfigureAwait(false);
        var builder = new StringBuilder();
        var depth = 0;
        var inString = false;
        var escaped = false;
        while (await PeekAsync(cancellationToken).ConfigureAwait(false) is var c && c >= 0)
        {
            if (!inString && depth == 0 && builder.Length > 0 && (c is ',' or '}' or ']' || char.IsWhiteSpace((char)c)))
            {
                break;
            }

            await TakeAsync(cancellationToken).ConfigureAwait(false);
            Append(builder, c);
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c is '{' or '[')
            {
                depth++;
            }
            else if (c is '}' or ']')
            {
                depth--;
            }

            if (depth == 0 && !inString && c is '}' or ']' or '"')
            {
                break;
            }
        }
        return builder.ToString();
    }

    private void Append(StringBuilder builder, int c)
    {
        if (builder.Length >= options.MaxRecordCharacters)
        {
            throw new InvalidDataException("Native record character limit exceeded; original remains in the source file.");
        }

        builder.Append((char)c);
    }

    public void Dispose() => _reader.Dispose();
}
