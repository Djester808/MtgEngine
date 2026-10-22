using System.Text.Json;
using MtgEngine.Api.Services.Community;


namespace MtgEngine.Api.Tests;

/// <summary>
/// The average deck parser: EDHREC writes the deck as [name, quantity] pairs grouped by card type.
/// </summary>
public sealed class EdhrecAverageDeckParserTests
{
    [Fact]
    public void The_deck_is_read_as_typed_sections_of_name_and_quantity()
    {
        using var document = EdhrecFixtures.Json(EdhrecFixtures.AverageDeck);
        var deck = EdhrecAverageDeckParser.Parse(document);

        var artifacts = deck.Sections.First(section => section.Type == "Artifact");
        Assert.Equal("Arcane Signet", artifacts.Cards[0].N);
        Assert.Equal(1, artifacts.Cards[0].Q);
        Assert.Null(artifacts.Cards[0].Id);
        Assert.Contains(deck.Sections, section => section.Type == "Land");
    }

    [Fact]
    public void The_deck_s_commanders_are_names()
    {
        using var document = EdhrecFixtures.Json(EdhrecFixtures.AverageDeck);
        var deck = EdhrecAverageDeckParser.Parse(document);

        Assert.Equal("Atraxa, Praetors' Voice", Assert.Single(deck.Commanders).N);
    }

    [Fact]
    public void The_bracket_and_budget_counts_ride_along()
    {
        using var document = EdhrecFixtures.Json(EdhrecFixtures.AverageDeck);
        var deck = EdhrecAverageDeckParser.Parse(document);

        using var counts = JsonDocument.Parse(deck.Counts);
        Assert.Equal(3857, counts.RootElement.GetProperty("bracket_counts").GetProperty("3").GetInt32());
        Assert.Equal(35841, counts.RootElement.GetProperty("budget_counts").GetProperty("middle").GetInt32());
    }

    [Fact]
    public void A_precon_row_with_a_set_and_a_collector_number_still_reads_as_a_pair()
    {
        using var document = JsonDocument.Parse("""
            {"deck":{"commander":["A Commander"],"cards":{"Creature":[["Llanowar Elves",2,"dom","168"]]}}}
            """);
        var deck = EdhrecAverageDeckParser.Parse(document);

        var card = Assert.Single(Assert.Single(deck.Sections).Cards);
        Assert.Equal("Llanowar Elves", card.N);
        Assert.Equal(2, card.Q);
    }

    [Fact]
    public void A_payload_without_a_deck_is_loud_and_stores_nothing()
    {
        using var document = JsonDocument.Parse("""{"deck":{"commander":["A Commander"]}}""");
        Assert.Throws<CommunityDataException>(() => EdhrecAverageDeckParser.Parse(document));
    }

    [Fact]
    public void The_stored_shape_cannot_change_without_a_version_bump() =>
        Assert.Equal("edhrec-average-v1", EdhrecAverageDeckParser.Version);
}
