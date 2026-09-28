namespace AgentExplorer.Core.Parsing;

internal sealed class MarkdownFence
{
    internal bool IsOpen => _length > 0;

    private char _character;
    private int _length;

    internal bool Observe(string line)
    {
        var span = line.AsSpan().TrimStart();
        if (span.Length < 3 || span[0] is not ('`' or '~'))
        {
            return false;
        }

        var count = 0;
        while (count < span.Length && span[count] == span[0])
        {
            count++;
        }

        if (count < 3)
        {
            return false;
        }

        if (_length == 0)
        {
            _character = span[0];
            _length = count;
            return true;
        }
        if (span[0] != _character || count < _length || !span[count..].Trim().IsEmpty)
        {
            return false;
        }

        _length = 0;
        return true;
    }
}
