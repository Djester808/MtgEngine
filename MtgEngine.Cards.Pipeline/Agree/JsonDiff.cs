using System.Globalization;
using System.Text.Json.Nodes;

namespace MtgEngine.Cards.Pipeline.Agree;

/// <summary>
/// Where two readings of one card first disagree, as a JSON path the dispute view can show:
/// <c>faces[0].abilities[1].do.kind: "Draw" vs "Mill"</c>. Depth first in the left reading's
/// property order, so the discriminator of a differing record is reported before its
/// parameters. Printed text is not a disagreement, so <see cref="Canonical"/> drops it.
/// </summary>
public static class JsonDiff
{
    /// <summary>How much of an object or array is shown either side of the "vs".</summary>
    private const int MaxValueLength = 80;

    /// <summary>The first difference between two definitions, ignoring the printed text fields, or null when they agree.</summary>
    public static string? Between(CardDefinition left, CardDefinition right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return Find(Canonical(left), Canonical(right), string.Empty);
    }

    /// <summary>The first difference between two JSON trees, or null when they are equal.</summary>
    public static string? Between(JsonNode? left, JsonNode? right) => Find(left, right, string.Empty);

    /// <summary>
    /// A definition as the agreement stage compares it: the serialized tree with every
    /// <c>text</c> and <c>oracleText</c> property removed, because two models quoting the same
    /// card differently have not disagreed about what it does.
    /// </summary>
    public static JsonNode Canonical(CardDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var node = JsonNode.Parse(CardFileSerializer.WriteDefinition(definition))
            ?? throw new InvalidOperationException("A definition serialized to JSON null.");
        Strip(node);
        return node;
    }

    /// <summary>The canonical form as a string, for equality and for a cache key.</summary>
    public static string CanonicalText(CardDefinition definition) => Canonical(definition).ToJsonString();

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("text");
                obj.Remove("oracleText");
                foreach (var child in obj.Select(p => p.Value).ToList())
                    Strip(child);
                break;
            case JsonArray arr:
                foreach (var child in arr)
                    Strip(child);
                break;
            default:
                break;
        }
    }

    private static string? Find(JsonNode? left, JsonNode? right, string path)
    {
        if (left is null && right is null)
            return null;
        if (left is null || right is null)
            return Line(path, left, right);

        if (left is JsonObject leftObject && right is JsonObject rightObject)
            return FindInObject(leftObject, rightObject, path);
        if (left is JsonArray leftArray && right is JsonArray rightArray)
            return FindInArray(leftArray, rightArray, path);
        if (left is JsonObject or JsonArray || right is JsonObject or JsonArray)
            return Line(path, left, right);

        return string.Equals(left.ToJsonString(), right.ToJsonString(), StringComparison.Ordinal)
            ? null
            : Line(path, left, right);
    }

    private static string? FindInObject(JsonObject left, JsonObject right, string path)
    {
        foreach (var (name, value) in left)
        {
            var child = Descend(path, name);
            if (!right.TryGetPropertyValue(name, out var other))
                return Line(child, value, null);
            var difference = Find(value, other, child);
            if (difference is not null)
                return difference;
        }

        foreach (var (name, value) in right)
        {
            if (left.ContainsKey(name))
                continue;
            return Line(Descend(path, name), null, value);
        }

        return null;
    }

    private static string? FindInArray(JsonArray left, JsonArray right, string path)
    {
        if (left.Count != right.Count)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{Where(path)}: {left.Count} items vs {right.Count} items");
        }

        for (var i = 0; i < left.Count; i++)
        {
            var difference = Find(left[i], right[i], string.Create(CultureInfo.InvariantCulture, $"{path}[{i}]"));
            if (difference is not null)
                return difference;
        }

        return null;
    }

    private static string Descend(string path, string name) => path.Length == 0 ? name : path + "." + name;

    private static string Where(string path) => path.Length == 0 ? "$" : path;

    private static string Line(string path, JsonNode? left, JsonNode? right) =>
        Where(path) + ": " + Show(left) + " vs " + Show(right);

    private static string Show(JsonNode? node)
    {
        if (node is null)
            return "absent";
        var text = node.ToJsonString();
        return text.Length <= MaxValueLength ? text : text[..MaxValueLength] + "…";
    }
}
