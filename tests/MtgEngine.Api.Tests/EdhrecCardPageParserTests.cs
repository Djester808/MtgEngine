using System.Text.Json;
using MtgEngine.Api.Services.Community;


namespace MtgEngine.Api.Tests;

/// <summary>
/// The card page parser: the card's own figures, the commanders that play it, and the per-type
/// lists of what is played beside it.
/// </summary>
public sealed class EdhrecCardPageParserTests
{
    [Fact]
    public void The_card_s_own_figures_come_from_the_page_s_card()
    {
        using var document = EdhrecFixtures.Json(EdhrecFixtures.Card);
        var page = EdhrecCardPageParser.Parse(document, "sol-ring");

        // Verified 2026-09-21: 8,394,290 of 10,077,368 decks, which is the site's 83.3 %.
        Assert.Equal(8394290, page.DeckCount);
        Assert.Equal(10077368, page.PotentialDecks);
        Assert.Equal("sol-ring", page.Slug);
        // The salt survey's average sits in the same object and is read with the digits printed.
        Assert.Equal(1.46268656716418m, page.Salt);
    }

    [Fact]
    public void The_commander_lists_are_cardlists_like_any_other()
    {
        using var document = EdhrecFixtures.Json(EdhrecFixtures.Card);
        var page = EdhrecCardPageParser.Parse(document, "sol-ring");

        Assert.Equal(["newcommanders", "topcommanders"], page.Lists.Take(2).Select(list => list.Tag));
        var top = page.Lists.Single(list => list.Tag == "topcommanders").Entries[0];
        Assert.Equal("Y'shtola, Night's Blessed", top.N);
        Assert.Equal(51251, top.D);
        Assert.Equal(54591, top.P);

        var newest = page.Lists.Single(list => list.Tag == "newcommanders").Entries[0];
        Assert.Equal("Thranduil, the Elvenking", newest.N);
        Assert.Equal(7673, newest.D);
    }

    [Fact]
    public void The_similar_cards_and_the_type_chart_are_kept()
    {
        using var document = EdhrecFixtures.Json(EdhrecFixtures.Card);
        var page = EdhrecCardPageParser.Parse(document, "sol-ring");

        Assert.Equal("Mana Vault", page.Similar[0].N);
        using var piechart = JsonDocument.Parse(page.Piechart);
        Assert.True(piechart.RootElement.GetProperty("piechart").GetProperty("content").GetArrayLength() > 0);
    }

    [Fact]
    public void A_payload_without_cardlists_is_loud_and_stores_nothing()
    {
        using var document = JsonDocument.Parse("""{"container":{"json_dict":{"card":{"num_decks":1}}}}""");
        Assert.Throws<CommunityDataException>(() => EdhrecCardPageParser.Parse(document, "sol-ring"));
    }

    [Fact]
    public void The_stored_shape_cannot_change_without_a_version_bump() =>
        Assert.Equal("edhrec-card-v2", EdhrecCardPageParser.Version);
}
