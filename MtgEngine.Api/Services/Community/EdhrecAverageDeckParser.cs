using System.Text.Json;


namespace MtgEngine.Api.Services.Community;

/// <summary>
/// Reads <c>pages/average-decks/{slug}[/{variant}].json</c>: the hundred cards most decks play.
/// </summary>
/// <remarks>
/// Changelog. <c>edhrec-average-v1</c> is the first stored shape. The deck is
/// <c>deck.cards {Type: [[name, quantity], …]}</c> — a pair, not an object — and a precon page adds
/// a set and a collector number to the same array, so a longer row is read for its first two places
/// and the rest ignored.
/// </remarks>
internal static class EdhrecAverageDeckParser
{
    /// <summary>The stored shape's name.</summary>
    public const string Version = "edhrec-average-v1";

    /// <summary>The deck, with every name still a name.</summary>
    public static EdhrecAverageDeck Parse(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = document.RootElement;
        if (!EdhrecPages.TryNavigate(root, out var cards, "deck", "cards")
            || cards.ValueKind != JsonValueKind.Object)
            throw new CommunityDataException("EDHREC average deck payload shape changed.");

        var sections = new List<EdhrecStoredSection>();
        int total = 0;
        foreach (var section in cards.EnumerateObject())
        {
            if (sections.Count >= EdhrecPages.MaxLists || total >= EdhrecPages.MaxEntriesPerPage)
                break;
            if (section.Value.ValueKind != JsonValueKind.Array)
                continue;
            var entries = new List<EdhrecStoredCard>();
            foreach (var pair in section.Value.EnumerateArray())
            {
                if (entries.Count >= EdhrecPages.MaxEntriesPerList || total >= EdhrecPages.MaxEntriesPerPage)
                    break;
                if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() == 0)
                    continue;
                var name = pair[0].ValueKind == JsonValueKind.String ? pair[0].GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                int quantity = pair.GetArrayLength() > 1 && pair[1].ValueKind == JsonValueKind.Number
                    && pair[1].TryGetInt32(out var read) ? read : 1;
                entries.Add(new EdhrecStoredCard(name, null, quantity < 1 ? 1 : quantity));
                total++;
            }
            sections.Add(new EdhrecStoredSection(section.Name, [.. entries]));
        }

        return new EdhrecAverageDeck(
            ReadCommanders(root),
            [.. sections],
            EdhrecPages.RawObject(root, "bracket_counts", "budget_counts"),
            EdhrecPages.RawObject(root, "prices"),
            Unresolved: 0);
    }

    /// <summary>The deck's commanders, written as plain names.</summary>
    private static EdhrecStoredName[] ReadCommanders(JsonElement root)
    {
        if (!EdhrecPages.TryNavigate(root, out var commanders, "deck", "commander")
            || commanders.ValueKind != JsonValueKind.Array)
            return [];
        var names = new List<EdhrecStoredName>();
        foreach (var item in commanders.EnumerateArray())
        {
            if (names.Count >= EdhrecPages.MaxSimilar)
                break;
            var name = item.ValueKind == JsonValueKind.String ? item.GetString() : EdhrecPages.Text(item, "name");
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(new EdhrecStoredName(name, null));
        }
        return [.. names];
    }
}
