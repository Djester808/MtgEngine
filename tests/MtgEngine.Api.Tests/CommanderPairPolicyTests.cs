using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

[Trait("Scope", "Deck")]
public sealed class CommanderPairPolicyTests
{
    [Theory]
    [InlineData("Partner", "Partner", true)]
    [InlineData("Partner", "Partner—Friends forever", false)]
    [InlineData("Partner—Friends forever", "Partner—Friends forever", true)]
    [InlineData("Partner—Survivors", "Partner—Friends forever", false)]
    [InlineData("Partner with B", "Partner with A", true)]
    [InlineData("Partner with B", "Partner with C", false)]
    [InlineData("", "", false)]
    public void Only_compatible_partner_abilities_pair(string first, string second, bool expected) =>
        Assert.Equal(expected, CommanderPairPolicy.CanPair(Card("A", first), Card("B", second)));

    [Fact]
    public void Background_pairs_with_choose_background_in_either_order()
    {
        var leader = Card("leader", "Choose a Background");
        var background = Card("background", "", CardType.Enchantment, ["Background"]);
        Assert.True(CommanderPairPolicy.CanPair(leader, background));
        Assert.True(CommanderPairPolicy.CanPair(background, leader));
        Assert.False(CommanderPairPolicy.CanPair(Card("other", "Partner"), background));
    }

    [Fact]
    public void Doctors_companion_requires_exact_doctor_creature_types()
    {
        var companion = Card("companion", "Doctor’s companion");
        Assert.True(CommanderPairPolicy.CanPair(companion, Card("doctor", "", subtypes: ["Time Lord", "Doctor"])));
        Assert.False(CommanderPairPolicy.CanPair(companion, Card("doctor", "", subtypes: ["Time Lord", "Doctor", "Human"])));
        Assert.False(CommanderPairPolicy.CanPair(companion, Card("doctor", "", subtypes: ["Doctor"])));
    }

    [Fact]
    public void The_same_card_cannot_be_both_commanders() =>
        Assert.False(CommanderPairPolicy.CanPair(Card("A", "Partner"), Card("A", "Partner")));

    private static CardDefinition Card(string name, string text, CardType type = CardType.Creature, string[]? subtypes = null) => new()
    {
        Name = name,
        OracleId = name,
        OracleText = text,
        CardTypes = type,
        Subtypes = subtypes ?? [],
        Supertypes = ["Legendary"],
        Legalities = new Dictionary<string, string> { ["commander"] = "legal" },
    };
}
