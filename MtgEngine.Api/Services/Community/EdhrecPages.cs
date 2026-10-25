using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;


namespace MtgEngine.Api.Services.Community;

/// <summary>One card on a page: the upstream name, the oracle id it resolved to, and its figures.</summary>
/// <remarks>
/// The field names are one letter because a commander page holds a thousand of them and the row is
/// read whole: n name, id oracle id, s synergy, d num_decks, p potential_decks, t trend_zscore.
/// Inclusion is never stored — it is d / p at read, so a later correction to either figure cannot
/// leave a stale third number behind.
/// </remarks>
internal sealed record EdhrecEntry(string N, Guid? Id, double? S, int? D, int? P, double? T);

/// <summary>One section of a page: EDHREC's <c>tag</c> and <c>header</c> over its cardviews.</summary>
internal sealed record EdhrecStoredList(string Tag, string Header, EdhrecEntry[] Entries);

/// <summary>A named card with no figures: a similar commander, or an average deck's commander.</summary>
internal sealed record EdhrecStoredName(string N, Guid? Id);

/// <summary>One theme a commander is played as, with how many decks play it that way.</summary>
internal sealed record EdhrecStoredTag(string Slug, string Name, int Count);

/// <summary>One card of an average deck: name, oracle id and quantity.</summary>
internal sealed record EdhrecStoredCard(string N, Guid? Id, int Q);

/// <summary>One type section of an average deck.</summary>
internal sealed record EdhrecStoredSection(string Type, EdhrecStoredCard[] Cards);

/// <summary>A commander's page as it is stored: upstream's numbers, nothing computed.</summary>
internal sealed record EdhrecCommanderPage(
    string Slug,
    int? DeckCount,
    int? Rank,
    EdhrecStoredList[] Lists,
    EdhrecStoredName[] Similar,
    EdhrecStoredTag[] Tags,
    string Counts,
    string Panels,
    int Unresolved);

/// <summary>A card's page as it is stored.</summary>
internal sealed record EdhrecCardPage(
    string Slug,
    int? DeckCount,
    int? PotentialDecks,
    decimal? Salt,
    EdhrecStoredList[] Lists,
    EdhrecStoredName[] Similar,
    string Piechart,
    int Unresolved);

/// <summary>An average deck as it is stored.</summary>
internal sealed record EdhrecAverageDeck(
    EdhrecStoredName[] Commanders,
    EdhrecStoredSection[] Sections,
    string Counts,
    string Prices,
    int Unresolved);

/// <summary>
/// The shared reading of an EDHREC page: the caps every stored list obeys, the cardlist walk both
/// page parsers use, and how the names on a page become oracle ids.
/// </summary>
internal static class EdhrecPages
{
    /// <summary>The most cards one stored list keeps.</summary>
    public const int MaxEntriesPerList = 200;

    /// <summary>The most cards one stored page keeps across all its lists.</summary>
    public const int MaxEntriesPerPage = 1000;

    /// <summary>The most sections one stored page keeps.</summary>
    public const int MaxLists = 100;

    /// <summary>The most similar cards or commanders one stored page keeps.</summary>
    public const int MaxSimilar = 20;

    /// <summary>The most themes one stored commander page keeps.</summary>
    public const int MaxTags = 200;

    /// <summary>Row payloads are camel-cased and drop their nulls: the rows are read whole.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);

    /// <summary>A stored payload that cannot be read counts as a miss, never as an empty page.</summary>
    public static bool TryRead<T>(string payload, out T value)
    {
        try
        {
            var read = JsonSerializer.Deserialize<T>(payload, Json);
            value = read!;
            return read is not null;
        }
        catch (JsonException)
        {
            value = default!;
            return false;
        }
    }

    /// <summary>
    /// The cardlists of a page, capped. A section without cardviews is kept for its header: the
    /// shape of a page is part of what is stored.
    /// </summary>
    public static EdhrecStoredList[] ReadLists(JsonElement cardlists)
    {
        var lists = new List<EdhrecStoredList>();
        int total = 0;
        foreach (var section in cardlists.EnumerateArray())
        {
            if (lists.Count >= MaxLists || total >= MaxEntriesPerPage)
                break;
            var tag = Text(section, "tag");
            var header = Text(section, "header");
            var entries = new List<EdhrecEntry>();
            if (section.TryGetProperty("cardviews", out var views) && views.ValueKind == JsonValueKind.Array)
                foreach (var view in views.EnumerateArray())
                {
                    if (entries.Count >= MaxEntriesPerList || total >= MaxEntriesPerPage)
                        break;
                    var name = Text(view, "name");
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    entries.Add(new EdhrecEntry(
                        name,
                        null,
                        Number(view, "synergy"),
                        Count(view, "num_decks"),
                        Count(view, "potential_decks"),
                        Number(view, "trend_zscore")));
                    total++;
                }
            lists.Add(new EdhrecStoredList(tag, header, [.. entries]));
        }
        return [.. lists];
    }

    /// <summary>
    /// A page's <c>similar</c> list. EDHREC writes it as plain names on a commander page and as
    /// objects elsewhere, so both spellings are read.
    /// </summary>
    public static EdhrecStoredName[] ReadSimilar(JsonElement root)
    {
        if (!root.TryGetProperty("similar", out var similar) || similar.ValueKind != JsonValueKind.Array)
            return [];
        var names = new List<EdhrecStoredName>();
        foreach (var item in similar.EnumerateArray())
        {
            if (names.Count >= MaxSimilar)
                break;
            var name = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : Text(item, "value") is { Length: > 0 } value ? value : Text(item, "name");
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(new EdhrecStoredName(name, null));
        }
        return [.. names];
    }

    /// <summary>The named properties of an element, copied verbatim into one JSON object.</summary>
    public static string RawObject(JsonElement source, params string[] keys)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (source.ValueKind == JsonValueKind.Object)
                foreach (var key in keys)
                    if (source.TryGetProperty(key, out var value)
                        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    {
                        writer.WritePropertyName(key);
                        value.WriteTo(writer);
                    }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Walks a path of property names; false the moment one is missing.</summary>
    public static bool TryNavigate(JsonElement root, out JsonElement result, params string[] path)
    {
        result = root;
        foreach (var key in path)
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(key, out result))
                return false;
        return true;
    }

    public static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>A count, clamped into an int: upstream counts run to eight figures, not ten.</summary>
    public static int? Count(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
            return null;
        return (int)Math.Clamp(number, int.MinValue, int.MaxValue);
    }

    /// <summary>
    /// A published figure read as a decimal, so it is stored and shown with the digits EDHREC
    /// printed rather than with a binary approximation of them.
    /// </summary>
    public static decimal? Decimal(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
            return null;
        return number;
    }

    public static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

}
