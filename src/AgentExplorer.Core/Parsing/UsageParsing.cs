using System.Text.Json;

using AgentExplorer.Core.Models;

namespace AgentExplorer.Core.Parsing;

internal static class UsageParsing
{
    internal static TokenUsage Tokens(JsonElement value) => new()
    {
        Input = value.Num("input_tokens") ?? value.Num("inputTokens") ?? value.Num("input"),
        Output = value.Num("output_tokens") ?? value.Num("outputTokens") ?? value.Num("output"),
        Total = value.Num("total_tokens") ?? value.Num("totalTokens") ?? value.Num("total"),
        CacheRead = value.Num("cached_input_tokens") ?? value.Num("cache_read_input_tokens") ??
            value.Num("cacheReadTokens") ?? value.Num("cacheReadInputTokens") ?? value.Get("cache").Num("read"),
        CacheWrite = value.Num("cache_write_input_tokens") ?? value.Num("cache_creation_input_tokens") ??
            value.Num("cacheWriteTokens") ?? value.Num("cacheCreationInputTokens") ?? value.Get("cache").Num("write"),
        Reasoning = value.Num("reasoning_output_tokens") ?? value.Num("reasoningTokens") ?? value.Num("thinkingTokens") ??
            value.Num("reasoning") ?? value.Get("output_tokens_details").Num("thinking_tokens")
    };
}
