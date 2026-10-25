using System.Text.Json;


namespace MtgEngine.Api.Services.Community;

/// <summary>One commander on EDHREC's top list: its name, its path slug, its deck count and its rank.</summary>
internal sealed record EdhrecTopCommander(string Name, string Slug, int DeckCount, int? Rank);

/// <summary>
/// Reads <c>pages/commanders/year.json</c> (and its pages <c>year-past2years-{n}.json</c>): EDHREC's
/// "Top Commanders · Past 2 Years" list.
/// </summary>
/// <remarks>
/// Shape verified with curl on 2026-10-07: <c>container.json_dict.cardlists[0]</c> is
/// <c>{header "Past 2 Years", tag "past2years", total_pages, more, cardviews[100]}</c> and each
/// cardview is <c>{id, name, sanitized, slug, url, num_decks, potential_decks (0), rank,
/// trend_zscore}</c>. The figures are the ones a commander's own page carries
/// (<c>container.json_dict.card.num_decks</c> and <c>rank</c>: Edgar Markov 51,527 decks, rank 2 on
/// both), so a row this list writes and a row a page writes say the same thing. A partner pair is
/// listed as "A // B"; it is not one card and does not bind.
/// </remarks>
internal static class EdhrecTopCommandersParser
{
    /// <summary>
    /// The list, in EDHREC's order. A page with no cardlists is a changed shape and throws; a list
    /// with no cardviews is an empty list. An entry without a name or a deck count is skipped.
    /// </summary>
    /// <exception cref="CommunityDataException">The payload is not the shape this parser reads.</exception>
    public static EdhrecTopCommander[] Parse(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        JsonElement views;
        if (document.RootElement.TryGetProperty("cardviews", out var continuation) && continuation.ValueKind == JsonValueKind.Array)
            views = continuation;
        else
        {
            if (!EdhrecPages.TryNavigate(document.RootElement, out var cardlists, "container", "json_dict", "cardlists") || cardlists.ValueKind != JsonValueKind.Array)
                throw new CommunityDataException("EDHREC top-commanders payload shape changed.");
            var section = cardlists.EnumerateArray().FirstOrDefault();
            if (section.ValueKind != JsonValueKind.Object || !section.TryGetProperty("cardviews", out views) || views.ValueKind != JsonValueKind.Array)
                return [];
        }
        var commanders = new List<EdhrecTopCommander>();
        foreach (var view in views.EnumerateArray().Take(EdhrecPages.MaxEntriesPerList))
        {
            var name = EdhrecPages.Text(view, "name");
            if (string.IsNullOrWhiteSpace(name) || EdhrecPages.Count(view, "num_decks") is not { } decks)
                continue;
            var slug = EdhrecPages.Text(view, "sanitized");
            commanders.Add(new(name, slug.Length > 0 ? slug : EdhrecPages.Text(view, "slug"), decks, EdhrecPages.Count(view, "rank")));
        }
        return [.. commanders];
    }
}
