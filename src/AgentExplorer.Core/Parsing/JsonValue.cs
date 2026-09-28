using System.Globalization;
using System.Text.Json;

namespace AgentExplorer.Core.Parsing;

internal static class JsonValue
{
    internal static JsonElement Get(this JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) ? child : default;

    internal static string? String(this JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null
    };

    internal static string? Str(this JsonElement value, string name) => value.Get(name).String();

    internal static long? Long(this JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    internal static long? Num(this JsonElement value, string name) => value.Get(name).Long();

    internal static decimal? Decimal(this JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;

    internal static bool? Bool(this JsonElement value, string name) => value.Get(name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    internal static IEnumerable<JsonElement> Items(this JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    internal static string? Raw(this JsonElement value) =>
        value.ValueKind == JsonValueKind.Undefined ? null : value.GetRawText();

    internal static JsonElement ParseArguments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException) { return default; }
    }

    internal static DateTimeOffset? Time(this JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var milliseconds))
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        var text = value.String();
        // Offset-free times (notably Markdown exports) have no unambiguous instant.
        if (text is null || !(text.EndsWith('Z') ||
            (text.Length >= 6 && text[^3] == ':' && text[^6] is '+' or '-')))
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
    }

    internal static string? ContentText(this JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var parts = value.EnumerateArray().Select(ContentText).Where(x => x is not null);
            var text = string.Join("\n", parts);
            return text.Length == 0 ? null : text;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return value.Str("text") ?? value.Str("thinking") ?? value.Get("content").ContentText();
    }

    internal static string PointerSegment(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);
}
