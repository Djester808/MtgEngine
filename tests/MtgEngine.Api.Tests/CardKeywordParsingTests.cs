using System.Text.Json;
using MtgEngine.Api.Mapping;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// A card's keywords reach the client and the AI fact sheet exactly as Scryfall spells them.
/// </summary>
/// <remarks>
/// They used to be folded into a <c>KeywordAbility</c> flags enum on the way in, and anything
/// the enum did not name became <c>None</c>. The enum knew 34 spellings; the bulk file holds
/// 890, so 12,399 of 38,633 cards arrived missing at least one — Enchant, Equip, Cycling,
/// Ward, Flashback, Kicker and Landfall among the most common.
/// <para>
/// Nothing failed loudly. The card modal showed a short keyword list, and
/// <c>CardFactSheet</c>'s "has X, named in the commander's text" fact could never fire for a
/// commander that named one of the missing ones. The suite could not see it either: the fact
/// sheet tests build their cards from string arrays directly, so the parser was never in the
/// path, and the two keywords they used — Menace and Flying — were both among the 34.
/// </para>
/// </remarks>
public class CardKeywordParsingTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static string Card(string keywords) => $$"""
    {
      "oracle_id": "kw",
      "name": "Test Card",
      "type_line": "Creature — Human",
      "oracle_text": "",
      "keywords": [{{keywords}}]
    }
    """;

    /// <summary>
    /// The keywords the old enum had no flag for, which is the overwhelming majority of them.
    /// </summary>
    [Theory]
    [InlineData("Ward")]
    [InlineData("Cycling")]
    [InlineData("Flashback")]
    [InlineData("Kicker")]
    [InlineData("Landfall")]
    [InlineData("Enchant")]
    [InlineData("Equip")]
    [InlineData("Crew")]
    [InlineData("Protection")]
    [InlineData("Scry")]
    public void A_keyword_the_old_enum_had_no_flag_for_still_arrives(string keyword)
    {
        var card = CardParser.Parse(Json(Card($"\"{keyword}\"")));

        Assert.NotNull(card);
        Assert.Equal([keyword], card.Keywords);
    }

    [Fact]
    public void Keywords_keep_Scryfalls_spelling_and_order()
    {
        var card = CardParser.Parse(Json(Card("\"First strike\", \"Ward\", \"Double strike\"")));

        Assert.NotNull(card);
        Assert.Equal(["First strike", "Ward", "Double strike"], card.Keywords);
    }

    [Fact]
    public void A_card_with_no_keywords_carries_an_empty_list()
    {
        var noProperty = CardParser.Parse(Json("""
        {"oracle_id":"none","name":"Plains","type_line":"Basic Land — Plains","oracle_text":""}
        """));

        Assert.NotNull(noProperty);
        Assert.Empty(noProperty.Keywords);
        Assert.Empty(CardParser.Parse(Json(Card("")))!.Keywords);
    }

    /// <summary>
    /// Scryfall has shipped a null and an empty string in this array before; neither is a
    /// keyword, and neither should reach a fact sheet as a blank bullet.
    /// </summary>
    [Fact]
    public void Blank_entries_are_dropped_rather_than_carried()
    {
        var card = CardParser.Parse(Json(Card("\"Ward\", \"\", \"   \", null")));

        Assert.NotNull(card);
        Assert.Equal(["Ward"], card.Keywords);
    }

    /// <summary>
    /// The whole server-side path a card takes to a client: Scryfall JSON through the parser
    /// and out of the one mapper every card endpoint returns through.
    /// </summary>
    /// <remarks>
    /// Curator of Mysteries as the bulk file prints it. The mapper used to rebuild this list by
    /// calling <c>ToString()</c> on the flags enum and splitting on commas, so it could only
    /// ever return names the enum had — Scry and Cycling reached the client as nothing at all.
    /// </remarks>
    [Fact]
    public void A_real_cards_keywords_survive_the_parser_and_the_mapper()
    {
        var card = CardParser.Parse(Json("""
        {
          "oracle_id": "curator",
          "name": "Curator of Mysteries",
          "type_line": "Creature — Sphinx",
          "oracle_text": "Flying\nWhenever you cycle or discard a card, scry 1.\nCycling {2}",
          "keywords": ["Scry", "Flying", "Cycling"]
        }
        """));

        Assert.NotNull(card);

        var dto = DomainMapper.ToDto(card);

        Assert.Equal(["Scry", "Flying", "Cycling"], dto.Keywords);
    }
}
