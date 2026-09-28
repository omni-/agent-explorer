namespace AgentExplorer.Core;

public sealed record ReadOptions
{
    /// <summary>Bounds each JSONL record, JSON message, or Markdown section. Exceeding it emits a diagnostic.</summary>
    public int MaxRecordCharacters { get; init; } = 32 * 1024 * 1024;

    /// <summary>Bounds decompressed input, including ZIP and Zstandard, independently of compressed size.</summary>
    public long MaxDecodedCharacters { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Only LoadAsync retains all events. Streaming APIs do not use this limit.</summary>
    public int MaxLoadedEvents { get; init; } = 1_000_000;

    public bool OrderByTimestamp { get; init; } = true;
}
