using System.Text.Json;


namespace MtgEngine.Api.Services.Community;

/// <summary>
/// Reads <c>pages/commanders/{slug}[/{variant}].json</c> into the row Catalog stores.
/// </summary>
/// <remarks>
/// Changelog. <c>edhrec-commander-v1</c> kept card names only, with the Basic-land sections already
/// dropped, and served one caller (the suggestion pool). <c>edhrec-commander-v2</c> keeps the page:
/// every section with its tag and header, each card's synergy, deck counts and trend, the theme
/// counts, the similar commanders, the bracket, budget and save-date counts, and the four panels
/// that carry a chart. Bump the version whenever the stored shape changes — a row whose version is
/// not the current one still serves, and is refetched before any fresh key is.
/// </remarks>
internal static class EdhrecCommanderPageParser
{
    /// <summary>The stored shape's name. A row written under another version is stale.</summary>
    public const string Version = "edhrec-commander-v2";

    /// <summary>
    /// The page, with every name still a name. A shape this parser does not recognise is a failure,
    /// not an empty page: nothing is stored and the caller answers 502.
    /// </summary>
    public static EdhrecCommanderPage Parse(JsonDocument document, string slug)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = document.RootElement;
        if (!EdhrecPages.TryNavigate(root, out var dictionary, "container", "json_dict")
            || !dictionary.TryGetProperty("cardlists", out var cardlists)
            || cardlists.ValueKind != JsonValueKind.Array)
            throw new CommunityDataException("EDHREC commander payload shape changed.");

        var card = dictionary.TryGetProperty("card", out var value) ? value : default;
        var panels = root.TryGetProperty("panels", out var found) ? found : default;

        return new EdhrecCommanderPage(
            slug,
            EdhrecPages.Count(card, "num_decks") ?? EdhrecPages.Count(root, "num_decks"),
            EdhrecPages.Count(card, "rank") ?? EdhrecPages.Count(root, "rank"),
            EdhrecPages.ReadLists(cardlists),
            EdhrecPages.ReadSimilar(root),
            ReadTags(root, panels),
            EdhrecPages.RawObject(root, "bracket_counts", "budget_counts", "savedate_counts"),
            EdhrecPages.RawObject(panels, "piechart", "mana_curve", "rank_over_time", "combocounts"),
            Unresolved: 0);
    }

    /// <summary>
    /// The themes this commander is played as. EDHREC repeats them in <c>panels.taglinks</c> and in
    /// top-level <c>tag_counts</c>; either will do, and the slug is what a theme variant is checked
    /// against, so a theme the page does not list can never reach an upstream path.
    /// </summary>
    private static EdhrecStoredTag[] ReadTags(JsonElement root, JsonElement panels)
    {
        var source = panels.ValueKind == JsonValueKind.Object
            && panels.TryGetProperty("taglinks", out var links) && links.ValueKind == JsonValueKind.Array
                ? links
                : root.TryGetProperty("tag_counts", out var counts) && counts.ValueKind == JsonValueKind.Array
                    ? counts
                    : default;
        if (source.ValueKind != JsonValueKind.Array)
            return [];

        var tags = new List<EdhrecStoredTag>();
        foreach (var item in source.EnumerateArray())
        {
            if (tags.Count >= EdhrecPages.MaxTags)
                break;
            var slug = EdhrecPages.Text(item, "slug");
            if (slug.Length == 0)
                continue;
            tags.Add(new EdhrecStoredTag(slug, EdhrecPages.Text(item, "value"), EdhrecPages.Count(item, "count") ?? 0));
        }
        return [.. tags];
    }
}
