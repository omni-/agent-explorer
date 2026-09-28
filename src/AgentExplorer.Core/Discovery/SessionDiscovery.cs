using System.IO.Compression;

using AgentExplorer.Core.Analysis;
using AgentExplorer.Core.IO;
using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Discovery;

/// <summary>Discovers predictable user data and configured exports without starting any agent runtime.</summary>
public sealed class SessionDiscovery
{
    public async Task<DiscoveryResult> DiscoverAsync(DiscoveryOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DiscoveryOptions();
        if (options.MetadataEventLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        var issues = new List<DiscoveryIssue>();
        var candidates = new List<SessionDescriptor>();
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var reader = new TranscriptReader();
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codex = Resolve(options.CodexHome, "CODEX_HOME", ".codex");
        var claude = Resolve(options.ClaudeHome, "CLAUDE_CONFIG_DIR", ".claude");
        var dsh = Resolve(options.DshHome, "DSH_HOME", ".dsh");
        var roots = new List<(string Root, TranscriptFormat? Format)>();
        if (codex is not null)
        {
            roots.Add((Path.Combine(codex, "sessions"), TranscriptFormat.CodexJsonl));
            roots.Add((Path.Combine(codex, "archived_sessions"), TranscriptFormat.CodexJsonl));
        }
        if (claude is not null)
        {
            roots.Add((Path.Combine(claude, "projects"), TranscriptFormat.ClaudeCodeJsonl));
        }

        if (dsh is not null)
        {
            roots.Add((Path.Combine(dsh, "sessions"), TranscriptFormat.DshJsonl));
        }

        roots.AddRange(options.ExportDirectories.Select(x => (x, (TranscriptFormat?)null)));
        foreach (var (root, knownFormat) in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Enumerate(root, issues))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCandidate(path, knownFormat) || !paths.Add(path))
                {
                    continue;
                }

                var inputs = new List<TranscriptInput>();
                try
                {
                    if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
                        inputs.AddRange(archive.Entries.Where(e => InputStream.IsDshEntry(e.FullName)).Select(e => new TranscriptInput(path, TranscriptFormat.DshJsonl, e.FullName)));
                    }
                    else
                    {
                        inputs.Add(new TranscriptInput(path, knownFormat));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { issues.Add(new(path, ex.Message)); }
                foreach (var input in inputs)
                {
                    try
                    {
                        var metadata = new SessionMetadata();
                        AgentSource? source = null;
                        var detected = await reader.DetectFormatAsync(input, cancellationToken).ConfigureAwait(false);
                        var count = 0;
                        await foreach (var item in reader.ReadEventsAsync(input with { Format = detected }, cancellationToken: cancellationToken).ConfigureAwait(false))
                        {
                            source = item.Source;
                            if (item.DuplicateOf is null && item.Metadata is { } update)
                            {
                                metadata = MetadataMerge.Apply(metadata, update);
                            }

                            if (item.Diagnostic is { Severity: not "information" } diagnostic)
                            {
                                issues.Add(new(path, diagnostic.Message));
                            }

                            if (++count >= options.MetadataEventLimit)
                            {
                                break;
                            }
                        }
                        if (source is not null)
                        {
                            candidates.Add(new(source.Value, metadata, input with { Format = detected }, []));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException || ex.GetType().Namespace == "ZstdSharp")
                    { issues.Add(new(path, ex.Message)); }
                }
            }
        }
        var sessions = candidates.GroupBy(x => (x.Source, Identity: x.Metadata.SessionId is { } id ? "id:" + id : "path:" + x.PreferredInput.Path + "!" + x.PreferredInput.ArchiveEntry))
            .Select(group =>
            {
                var ordered = group.OrderByDescending(x => Fidelity(x.PreferredInput.Format)).ThenBy(x => x.PreferredInput.Path, StringComparer.Ordinal).ThenBy(x => x.PreferredInput.ArchiveEntry, StringComparer.Ordinal).ToArray();
                return ordered[0] with { AlternativeInputs = ordered.Skip(1).Select(x => x.PreferredInput).ToArray() };
            }).OrderBy(x => x.Source).ThenBy(x => x.Metadata.SessionId, StringComparer.Ordinal).ThenBy(x => x.PreferredInput.Path, StringComparer.Ordinal).ToArray();
        return new DiscoveryResult(sessions, issues);

        string? Resolve(string? supplied, string variable, string fallback)
        {
            if (supplied is not null)
            {
                return string.IsNullOrWhiteSpace(supplied) ? null : Path.GetFullPath(supplied);
            }

            if (!options.UseDefaultLocations)
            {
                return null;
            }

            var environment = Environment.GetEnvironmentVariable(variable);
            return Path.GetFullPath(string.IsNullOrWhiteSpace(environment) ? Path.Combine(user, fallback) : environment);
        }
    }

    private static int Fidelity(TranscriptFormat? format) => format == TranscriptFormat.OpenCodeMarkdown ? 0 : 1;

    private static bool IsCandidate(string path, TranscriptFormat? format) => format switch
    {
        TranscriptFormat.CodexJsonl => Path.GetFileName(path).StartsWith("rollout-", StringComparison.Ordinal) && path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase),
        TranscriptFormat.ClaudeCodeJsonl => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase),
        TranscriptFormat.DshJsonl => Path.GetFileName(path).StartsWith("session", StringComparison.Ordinal) && (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jsonl.zstd", StringComparison.OrdinalIgnoreCase)),
        _ => Path.GetExtension(path).ToLowerInvariant() is ".json" or ".jsonl" or ".md" or ".zip" or ".zstd" or ".zst"
    };

    private static IEnumerable<string> Enumerate(string root, List<DiscoveryIssue> issues)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var queue = new Stack<string>();
        queue.Push(Path.GetFullPath(root));
        while (queue.TryPop(out var directory))
        {
            string[] files;
            try { files = Directory.GetFileSystemEntries(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issues.Add(new(directory, ex.Message)); continue; }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var path in files)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issues.Add(new(path, ex.Message)); continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    queue.Push(path);
                }
                else
                {
                    yield return path;
                }
            }
        }
    }
}
