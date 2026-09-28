namespace AgentExplorer.Core.IO;

internal sealed class SnapshotStream(Stream inner, long length) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    private long _position;

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, (int)Math.Min(count, length - _position));
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, length - _position)], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
