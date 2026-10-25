using System.Text.Json;


namespace MtgEngine.Api.Services.Community;

/// <summary>
/// Reads <c>pages/cards/{slug}.json</c>: who plays this card and what they play beside it.
/// </summary>
/// <remarks>
/// Changelog. <c>edhrec-card-v1</c> is the first stored shape. The card's own figures are in
/// <c>container.json_dict.card</c> (<c>num_decks</c> and <c>potential_decks</c>; Sol Ring reads
/// 8,394,290 of 10,077,368 on 2026-09-21), not at the top level, which is where a reader looking
/// for them would try first. The commander lists are ordinary cardlists whose cardviews name
/// commanders, so one walk serves both kinds of list.
/// <c>edhrec-card-v2</c> keeps <c>card.salt</c> beside them — the 0-4 average of EDHREC's annual
/// salt survey, which sits in the same object and was simply not read before (Sol Ring 1.4627).
/// It is stored in its own column rather than in the payload, because the salt directory sorts on
/// it and a sort has to be SQL.
/// </remarks>
internal static class EdhrecCardPageParser
{
    /// <summary>The stored shape's name.</summary>
    public const string Version = "edhrec-card-v2";

    /// <summary>The page, with every name still a name.</summary>
    public static EdhrecCardPage Parse(JsonDocument document, string slug)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = document.RootElement;
        if (!EdhrecPages.TryNavigate(root, out var dictionary, "container", "json_dict")
            || !dictionary.TryGetProperty("cardlists", out var cardlists)
            || cardlists.ValueKind != JsonValueKind.Array)
            throw new CommunityDataException("EDHREC card payload shape changed.");

        var card = dictionary.TryGetProperty("card", out var value) ? value : default;
        var panels = root.TryGetProperty("panels", out var found) ? found : default;

        return new EdhrecCardPage(
            slug,
            EdhrecPages.Count(card, "num_decks") ?? EdhrecPages.Count(root, "num_decks"),
            EdhrecPages.Count(card, "potential_decks") ?? EdhrecPages.Count(root, "potential_decks"),
            EdhrecPages.Decimal(card, "salt") ?? EdhrecPages.Decimal(root, "salt"),
            EdhrecPages.ReadLists(cardlists),
            EdhrecPages.ReadSimilar(root),
            EdhrecPages.RawObject(panels, "piechart"),
            Unresolved: 0);
    }
}
