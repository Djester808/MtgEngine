using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Eligibility gates the paid suggestions endpoint — an ineligible oracle id must not
/// become a distinct AI-generation cache key.
/// </summary>
public class CommanderRulesTests
{
    private static CardDefinition Card(
        CardType types = CardType.Creature,
        string[]? supertypes = null,
        string legality = "legal",
        string? oracleText = null) => new()
    {
        OracleId = "oracle-1",
        Name = "Test Card",
        CardTypes = types,
        Supertypes = [.. supertypes ?? []],
        Legalities = new Dictionary<string, string> { ["commander"] = legality },
        OracleText = oracleText ?? string.Empty,
    };

    [Fact]
    public void LegendaryCreature_IsEligible()
    {
        Assert.True(CommanderRules.IsCommanderEligible(Card(supertypes: ["Legendary"])));
    }

    [Fact]
    public void NonLegendaryCreature_IsNot()
    {
        Assert.False(CommanderRules.IsCommanderEligible(Card()));
    }

    [Fact]
    public void CanBeYourCommanderText_IsEligible()
    {
        Assert.True(CommanderRules.IsCommanderEligible(
            Card(types: CardType.Planeswalker, oracleText: "This card can be your commander.")));
    }

    [Fact]
    public void Tokens_AreNot()
    {
        Assert.False(CommanderRules.IsCommanderEligible(
            Card(types: CardType.Creature | CardType.Token, supertypes: ["Legendary"])));
    }

    [Theory]
    [InlineData("banned")]
    [InlineData("not_legal")]
    public void NotLegalInCommander_IsNot(string legality)
    {
        Assert.False(CommanderRules.IsCommanderEligible(
            Card(supertypes: ["Legendary"], legality: legality)));
    }
}
