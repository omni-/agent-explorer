using System.Security.Cryptography;

using AgentExplorer.Core.IO;

namespace AgentExplorer.Core.Indexing;

internal sealed class InputSnapshot : IDisposable
{
    internal string ContentSha256 { get; private set; } = "";
    internal long ByteLength { get; }

    private readonly FileStream _source;
    private string? _temporaryPath;

    private InputSnapshot(string path)
    {
        _source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        ByteLength = _source.Length;
    }

    internal static async Task<InputSnapshot> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var snapshot = new InputSnapshot(path);
        try
        {
            using var bounded = new SnapshotStream(snapshot._source, snapshot.ByteLength);
            snapshot.ContentSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(bounded, cancellationToken).ConfigureAwait(false));
            if (bounded.Position != snapshot.ByteLength)
            {
                throw new IOException($"Input shrank while being hashed: {path}");
            }
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    internal async Task<string> CopyAsync(CancellationToken cancellationToken)
    {
        if (_temporaryPath is not null)
        {
            return _temporaryPath;
        }

        // Keep the extension so the normal reader still recognizes ZIP and Zstandard containers.
        var temporaryPath = Path.Combine(Path.GetTempPath(), "AgentExplorer.Index." + Guid.NewGuid().ToString("N") + Path.GetExtension(_source.Name));
        _source.Position = 0;
        using var bounded = new SnapshotStream(_source, ByteLength);
        try
        {
            await using var copy = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await bounded.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            copy.Position = 0;
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(copy, cancellationToken).ConfigureAwait(false));
            if (copy.Length != ByteLength || hash != ContentSha256)
            {
                throw new IOException($"Input changed while being copied: {_source.Name}. Retry the scan.");
            }
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
        _temporaryPath = temporaryPath;
        return _temporaryPath;
    }

    public void Dispose()
    {
        _source.Dispose();
        if (_temporaryPath is not null)
        {
            File.Delete(_temporaryPath);
        }
    }
}
