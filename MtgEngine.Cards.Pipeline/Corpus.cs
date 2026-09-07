using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace MtgEngine.Cards.Pipeline;

/// <summary>One printed face as the translator sees it: the fields the model needs and nothing else.</summary>
public sealed record CorpusFace(string Name, string TypeLine, string? ManaCost, string OracleText, string? Power, string? Toughness, string? Loyalty, string? Defense);

/// <summary>One card from the Scryfall oracle file with its tokens resolved through all_parts.</summary>
public sealed record CorpusCard(
    string OracleId,
    string Name,
    string Layout,
    IReadOnlyList<CorpusFace> Faces,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<CorpusToken> Tokens)
{
    public string OracleHash => OracleHashing.Of(Faces.Select(f => f.OracleText));

    public bool IsFreeSlice => Faces.All(f => Templates.IsKeywordOnly(f, Keywords) || Templates.IsManaPattern(f));
}

public sealed record CorpusToken(string OracleId, string Name, string TypeLine, string? Colors, string OracleText, string? Power, string? Toughness);

/// <summary>Reads the Scryfall bulk files the API already downloads. JSON Lines, one card per line.</summary>
public static class Corpus
{
    public static string DefaultPath(string repoRoot) =>
        Path.Combine(repoRoot, "MtgEngine.Api", "bin", "Debug", "net10.0", "bulk-data", "oracle_cards.json");

    public static IReadOnlyDictionary<string, JsonElement> LoadRaw(string path)
    {
        var byOracle = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim().TrimEnd(',');
            if (trimmed is "" or "[" or "]")
                continue;
            JsonElement card;
            try
            {
                card = JsonDocument.Parse(trimmed).RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            if (card.TryGetProperty("oracle_id", out var oid) && oid.ValueKind == JsonValueKind.String)
                byOracle[oid.GetString()!] = card;
        }

        return byOracle;
    }

    public static CorpusCard? Card(IReadOnlyDictionary<string, JsonElement> raw, string oracleId)
    {
        if (!raw.TryGetValue(oracleId, out var card))
            return null;
        var faces = new List<CorpusFace>();
        if (card.TryGetProperty("card_faces", out var cf) && cf.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in cf.EnumerateArray())
                faces.Add(Face(f));
        }
        else
        {
            faces.Add(Face(card));
        }

        var keywords = card.TryGetProperty("keywords", out var kw) && kw.ValueKind == JsonValueKind.Array
            ? kw.EnumerateArray().Select(k => k.GetString() ?? string.Empty).Where(k => k.Length > 0).ToList()
            : [];

        var tokens = new List<CorpusToken>();
        if (card.TryGetProperty("all_parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("component", out var comp) && comp.GetString() == "token"
                    && part.TryGetProperty("id", out var id))
                {
                    var token = raw.Values.FirstOrDefault(c => c.TryGetProperty("id", out var cid) && cid.GetString() == id.GetString());
                    if (token.ValueKind == JsonValueKind.Object)
                        tokens.Add(Token(token));
                }
            }
        }

        return new CorpusCard(
            oracleId,
            card.GetProperty("name").GetString() ?? string.Empty,
            card.TryGetProperty("layout", out var layout) ? layout.GetString() ?? "normal" : "normal",
            faces,
            keywords,
            tokens);
    }

    private static CorpusFace Face(JsonElement f) => new(
        Str(f, "name") ?? string.Empty,
        Str(f, "type_line") ?? string.Empty,
        Str(f, "mana_cost"),
        Str(f, "oracle_text") ?? string.Empty,
        Str(f, "power"),
        Str(f, "toughness"),
        Str(f, "loyalty"),
        Str(f, "defense"));

    private static CorpusToken Token(JsonElement t) => new(
        Str(t, "oracle_id") ?? string.Empty,
        Str(t, "name") ?? string.Empty,
        Str(t, "type_line") ?? string.Empty,
        t.TryGetProperty("colors", out var c) && c.ValueKind == JsonValueKind.Array ? string.Join(string.Empty, c.EnumerateArray().Select(x => x.GetString())) : null,
        Str(t, "oracle_text") ?? string.Empty,
        Str(t, "power"),
        Str(t, "toughness"));

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Oracle ids in users' decks, most-played first. The demand-first ordering the plan calls for.</summary>
    public static IReadOnlyList<string> DeckOracleIds(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cc.OracleId, COUNT(DISTINCT cc.CollectionId) AS n
            FROM CollectionCards cc JOIN Collections c ON c.Id = cc.CollectionId
            WHERE c.IsDeck = 1
            GROUP BY cc.OracleId ORDER BY n DESC, cc.OracleId
            """;
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }
}

/// <summary>The free slice: faces the JSON alone describes. The pilot skips these; P2 writes them as templates.</summary>
public static partial class Templates
{
    public static bool IsKeywordOnly(CorpusFace face, IReadOnlyList<string> keywords)
    {
        var lines = face.OracleText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var known = keywords.Select(k => k.ToLowerInvariant()).ToHashSet();
        foreach (var raw in lines)
        {
            var line = Reminder().Replace(raw, string.Empty).Trim().TrimEnd('.');
            if (line.Length == 0)
                continue;
            foreach (var part in KeywordSplit().Split(line))
            {
                var head = part.Trim().Split(' ')[0].ToLowerInvariant();
                var whole = part.Trim().ToLowerInvariant();
                if (!known.Contains(whole) && !known.Contains(head))
                    return false;
            }
        }

        return true;
    }

    public static bool IsManaPattern(CorpusFace face)
    {
        var lines = face.OracleText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 && lines.All(l => Mana().IsMatch(Reminder().Replace(l, string.Empty).Trim()));
    }

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Reminder();

    [GeneratedRegex(@",\s*(?=[A-Z])")]
    private static partial Regex KeywordSplit();

    [GeneratedRegex(@"^(\{T\}: Add (\{[WUBRGC]\})+(( or | and/or )(\{[WUBRGC]\})+)*\.|\{T\}: Add one mana of any color\.|\{T\}: Add one mana of any color in your commander's color identity\.|This land enters tapped\.|\{T\}, Pay 1 life: Add (\{[WUBRGC]\})+( or (\{[WUBRGC]\})+)*\.)$")]
    private static partial Regex Mana();
}
