using System.IO.Compression;

using AgentExplorer.Core.Models;

using ZstdSharp;

namespace AgentExplorer.Core.IO;

internal sealed class InputStream : IDisposable
{
    internal Stream Stream { get; }
    internal string Path { get; }
    internal string? Entry { get; }

    private readonly FileStream _file;
    private readonly ZipArchive? _archive;

    internal InputStream(TranscriptInput input)
    {
        Path = System.IO.Path.GetFullPath(input.Path);
        _file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.SequentialScan);
        try
        {
            // A read of an active log ends at the length observed on open.
            Stream stream = new SnapshotStream(_file, _file.Length);
            if (Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                _archive = new ZipArchive(_file, ZipArchiveMode.Read, true);
                var entries = _archive.Entries.Where(e => IsDshEntry(e.FullName)).ToArray();
                var roots = entries.Where(e => !e.FullName.Contains('/') && !e.FullName.Contains('\\')).ToArray();
                var entry = input.ArchiveEntry is not null ? _archive.GetEntry(input.ArchiveEntry) :
                    roots.Length == 1 ? roots[0] : entries.Length == 1 ? entries[0] : null;
                if (entry is null || !IsDshEntry(entry.FullName))
                {
                    throw new InvalidDataException("Specify ArchiveEntry for a ZIP with zero or multiple DSH session members.");
                }

                Entry = entry.FullName;
                stream = entry.Open();
            }
            var name = Entry ?? Path;
            Stream = name.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)
                ? new DecompressionStream(stream) : stream;
        }
        catch
        {
            _archive?.Dispose();
            _file.Dispose();
            throw;
        }
    }

    internal static bool IsDshEntry(string name) =>
        name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".jsonl.zstd", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        Stream.Dispose();
        _archive?.Dispose();
        _file.Dispose();
    }
}
