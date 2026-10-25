using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

public sealed class RulesCoverageTests
{
    [Fact]
    public void A_literal_token_is_projected_as_rules_defined_without_a_deck_oracle_id()
    {
        var state = GameReducer.Replay([new GameStarted(
            [new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 42, 40, "test")]);
        state = new ZoneSystem().CreateToken(state,
            new TokenSpec("Saproling", Color.Green, CardType.Creature, Power: 1, Toughness: 1),
            new PlayerId(0), false, false).State;
        var view = new ViewProjector(new LayerSystem(), _ => false).Project(state, new PlayerId(0));
        var token = Assert.Single(view.Battlefield);
        Assert.True(token.IsToken);
        Assert.True(token.Enforced);
        Assert.Equal(1, token.Power);
    }

    [Theory]
    [InlineData(false, "", false, false)]
    [InlineData(true, "", false, true)]
    [InlineData(true, "copied-card", false, false)]
    [InlineData(true, "copied-card", true, true)]
    [InlineData(false, "card", true, true)]
    public void Copies_and_ordinary_cards_keep_the_oracle_coverage_policy(bool token, string oracleId, bool admitted, bool expected)
    {
        var definition = new CardDefinition(oracleId, "Fixture", Layout.Normal,
            [new Face("Fixture", FaceRole.Main, new TypeLine(CardType.Creature, Supertype.None, []))]);
        var obj = new GameObject(new ObjectId(1), definition, 0, new PlayerId(0), new PlayerId(0),
            ZoneKey.Shared(Zone.Battlefield), 1)
        { IsToken = token };
        Assert.Equal(expected, RulesCoverage.IsEnforced(obj, _ => admitted));
    }
}
