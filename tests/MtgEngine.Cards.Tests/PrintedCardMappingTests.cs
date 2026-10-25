using System.Text.Json;

namespace MtgEngine.Cards.Tests;

public sealed class PrintedCardMappingTests
{
    [Fact]
    public void Catalog_faces_preserve_layout_text_variable_stats_and_color_indicators_without_inventing_abilities()
    {
        using var source = JsonDocument.Parse("""
            {"oracle_id":"fixture", "name":"Front // Back", "layout":"transform", "card_faces":[
              {"name":"Front", "type_line":"Legendary Creature — Human Wizard", "mana_cost":"{1}{U}",
               "power":"*", "toughness":"1+*", "oracle_text":"Rules for front."},
              {"name":"Back", "type_line":"Creature — Spirit", "color_indicator":["U","R"],
               "power":"4", "toughness":"4", "oracle_text":"Rules for back."}
            ]}
            """);
        var card = Assert.IsType<CardDefinition>(PrintedCardMapping.Read(source.RootElement));
        Assert.Equal(Layout.Transform, card.Layout);
        Assert.Equal(2, card.Faces.Count);
        Assert.Equal(new PrintedStat("*"), card.Faces[0].Power);
        Assert.Equal(new PrintedStat("1+*"), card.Faces[0].Toughness);
        Assert.Equal(Supertype.Legendary, card.Faces[0].Types.Supertypes);
        Assert.Equal("{1}{U}", card.Faces[0].ManaCost);
        Assert.Equal(FaceRole.Back, card.Faces[1].Role);
        Assert.Equal(Color.Blue | Color.Red, card.Faces[1].ColorIndicator);
        Assert.Null(card.Faces[1].ManaCost);
        Assert.Equal("Rules for back.", card.Faces[1].OracleText);
        Assert.All(card.Faces, face => Assert.Empty(face.AbilityList));
    }

    [Theory]
    [InlineData("normal", "Creature — Time Lord", true)]
    [InlineData("normal", "Unknown — Wizard", false)]
    [InlineData("unknown-layout", "Creature", false)]
    [InlineData("transform", "Creature", false)]
    public void Unrepresentable_source_is_refused_instead_of_guessed(string layout, string types, bool accepted)
    {
        using var source = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            oracle_id = "fixture",
            name = "Fixture",
            layout,
            type_line = types,
        }));
        var definition = PrintedCardMapping.Read(source.RootElement);
        Assert.Equal(accepted, definition is not null);
        if (accepted)
            Assert.Equal("Time Lord", Assert.Single(definition!.Faces[0].Types.SubtypeList));
    }
}
