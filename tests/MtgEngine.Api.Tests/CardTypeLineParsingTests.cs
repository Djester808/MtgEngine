using System.Text.Json;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// A printed type line describes one card, and a double-faced card prints two of them with
/// "//" between. Which half a characteristic comes from is the whole of what these pin.
/// </summary>
public class CardTypeLineParsingTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private const string Delver = """
    {
      "oracle_id": "delver",
      "name": "Delver of Secrets // Insectile Aberration",
      "type_line": "Creature \u2014 Human Wizard // Creature \u2014 Human Insect",
      "oracle_text": ""
    }
    """;

    /// <summary>
    /// The subtypes of a two-faced card are its front face's (CR 712.4a).
    /// </summary>
    /// <remarks>
    /// The parser took everything after the <em>first</em> dash, which on a two-faced line runs
    /// to the end of the second face. Delver of Secrets was therefore an Insect while it was
    /// still a Human Wizard — and 642 cards in the corpus carried a subtype literally named
    /// "//".
    /// <para>
    /// Nothing crashed and no card failed to load: a tribal effect simply counted permanents it
    /// should not have, on the faces of cards nobody was looking at.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_two_faced_cards_subtypes_come_from_its_front_face_alone()
    {
        var card = CardParser.Parse(Json(Delver));

        Assert.NotNull(card);
        Assert.Equal(["Human", "Wizard"], card!.Subtypes);

        // The back face's own subtypes, and the separator, are what leaked.
        Assert.DoesNotContain("Insect", card.Subtypes);
        Assert.DoesNotContain("//", card.Subtypes);
        Assert.DoesNotContain("Creature", card.Subtypes);
    }

    /// <summary>
    /// A card is legendary only if its front face says so (CR 205.4).
    /// </summary>
    /// <remarks>
    /// Quieter than the subtype leak and worse where it bit: the legend rule is a state-based
    /// action, so a non-legendary front face wrongly marked legendary is one copy away from
    /// being put into a graveyard by rule (CR 704.5j).
    /// </remarks>
    [Fact]
    public void A_back_face_does_not_make_the_front_face_legendary()
    {
        var card = CardParser.Parse(Json("""
        {
          "oracle_id": "wildblood",
          "name": "Wildblood Pack",
          "type_line": "Creature \u2014 Werewolf // Legendary Creature \u2014 Werewolf",
          "oracle_text": ""
        }
        """));

        Assert.NotNull(card);
        Assert.DoesNotContain("Legendary", card!.Supertypes);
    }

    /// <summary>A single-faced line is unaffected by any of this.</summary>
    [Fact]
    public void A_single_faced_line_still_reads_every_subtype_it_prints()
    {
        var card = CardParser.Parse(Json("""
        {
          "oracle_id": "goblin",
          "name": "Test Goblin",
          "type_line": "Legendary Creature \u2014 Goblin Warrior",
          "oracle_text": ""
        }
        """));

        Assert.NotNull(card);
        Assert.Equal(["Goblin", "Warrior"], card!.Subtypes);
        Assert.Contains("Legendary", card.Supertypes);
    }
}
