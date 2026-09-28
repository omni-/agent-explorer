using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentExplorer.Core.Models;

/// <summary>Unmodified decoded native text, shared by all events expanded from this record.</summary>
public sealed record NativeRecord(SourceLocation Location, string Text)
{
    /// <summary>Parse the original record. The caller owns the returned document.</summary>
    public JsonDocument ParseJson() => JsonDocument.Parse(Text);

    public string ComputeSha256() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
}
