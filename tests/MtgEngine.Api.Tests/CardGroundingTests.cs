using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The commander-deck validation ladder previously lived as two untested inline copies
/// in AiBuildService (build + refine). These tests pin the shared rules.
/// </summary>
public class CardGroundingTests
{
    private static CardDefinition Card(
        ManaColor[]? identity = null,
        string legality = "legal",
        bool gameChanger = false) => new()
    {
        OracleId = "oracle-1",
        Name = "Test Card",
        CardTypes = CardType.Creature,
        ColorIdentity = [.. identity ?? [ManaColor.Red]],
        Legalities = new Dictionary<string, string> { ["commander"] = legality },
        GameChanger = gameChanger,
    };

    private static readonly IReadOnlySet<ManaColor> RedCommander = new HashSet<ManaColor>
    {
        ManaColor.Red,
    };

    [Fact]
    public void InIdentityCard_Passes()
    {
        Assert.Null(CardGrounding.ValidateForCommanderDeck(Card(), RedCommander, bracket: 3));
    }

    [Fact]
    public void OffColorCard_IsRejected()
    {
        var result = CardGrounding.ValidateForCommanderDeck(
            Card(identity: [ManaColor.Green]), RedCommander, bracket: 3);
        Assert.Equal(CardGrounding.Rejection.ColorIdentity, result);
    }

    [Fact]
    public void ColorlessIsNeverAViolation()
    {
        var result = CardGrounding.ValidateForCommanderDeck(
            Card(identity: [ManaColor.Colorless]), RedCommander, bracket: 3);
        Assert.Null(result);
    }

    [Fact]
    public void EmptyCommanderIdentity_SkipsTheColorCheck()
    {
        var result = CardGrounding.ValidateForCommanderDeck(
            Card(identity: [ManaColor.Green]), new HashSet<ManaColor>(), bracket: 3);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("banned")]
    [InlineData("not_legal")]
    public void NotCommanderLegal_IsRejected(string legality)
    {
        var result = CardGrounding.ValidateForCommanderDeck(
            Card(legality: legality), RedCommander, bracket: 3);
        Assert.Equal(CardGrounding.Rejection.NotCommanderLegal, result);
    }

    [Fact]
    public void GameChangerUnderBracketFour_IsRejected()
    {
        var result = CardGrounding.ValidateForCommanderDeck(
            Card(gameChanger: true), RedCommander, bracket: 3);
        Assert.Equal(CardGrounding.Rejection.AboveBracket, result);
    }

    [Fact]
    public void GameChangerAtBracketFour_Passes()
    {
        Assert.Null(CardGrounding.ValidateForCommanderDeck(
            Card(gameChanger: true), RedCommander, bracket: 4));
    }

    [Fact]
    public void ColorIdentityRejectsBeforeLegality()
    {
        // The ladder order is part of the contract — rejection counters key off the
        // first failed rule.
        var result = CardGrounding.ValidateForCommanderDeck(
            Card(identity: [ManaColor.Green], legality: "banned"), RedCommander, bracket: 3);
        Assert.Equal(CardGrounding.Rejection.ColorIdentity, result);
    }
}
