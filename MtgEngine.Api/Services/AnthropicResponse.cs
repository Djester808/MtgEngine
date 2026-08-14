using System.Text.Json;

namespace MtgEngine.Api.Services;

/// <summary>
/// Parsing helpers for Anthropic Messages API responses.
/// </summary>
/// <remarks>
/// Every AI service asks the model for JSON in the prompt, which means the response
/// is only JSON by convention: the model may wrap it in markdown fences or add a
/// sentence of preamble. These helpers centralise the salvage logic so all five
/// services behave identically.
/// </remarks>
internal static class AnthropicResponse
{
    /// <summary>
    /// Returns the text of the first <c>text</c> content block, or empty if none.
    /// </summary>
    /// <remarks>
    /// Deliberately scans for the first block of type "text" rather than taking
    /// content[0]. The first block is not guaranteed to be text -- enabling extended
    /// thinking or a server-side tool puts a different block type first, which would
    /// make a positional lookup throw.
    /// </remarks>
    public static string ExtractText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);

        if (!doc.RootElement.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.ValueEquals("text")
                && block.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Extracts the first complete JSON object from text that may carry markdown
    /// fences, preamble, or trailing commentary. Returns "{}" when no object is found.
    /// </summary>
    /// <remarks>
    /// Tracks string state and escapes so braces inside string literals -- e.g. a card
    /// name or reason containing '{' -- do not terminate the object early.
    /// </remarks>
    public static string ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "{}";

        int start = text.IndexOf('{');
        if (start < 0)
            return "{}";

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (c == '{')
                depth++;
            else if (c == '}' && --depth == 0)
                return text[start..(i + 1)];
        }

        // Unbalanced: return from '{' to the end and let the caller's deserialiser fail.
        return text[start..];
    }

    /// <summary>
    /// Convenience: response JSON -&gt; first text block -&gt; first JSON object -&gt; T.
    /// Returns default when the payload cannot be parsed.
    /// </summary>
    public static T? DeserializeJson<T>(string responseJson)
    {
        try
        {
            // ExtractText parses responseJson itself, so it belongs inside the guard —
            // a 2xx body that is not JSON (proxy HTML, truncated stream) used to throw
            // straight past this method's documented "returns default" contract.
            var json = ExtractJsonObject(ExtractText(responseJson));
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    internal static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };
}
