using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The player-scoped continuous effects the layer system folds into <see cref="PlayerLimits"/>.
///
/// <c>ExtraLandDrops</c> is the one this file was written for: the vocabulary has carried it
/// since wave 0, four card files in the corpus use it, and it had NO reader anywhere in the
/// solution — the land ceiling was a hardcoded 1. Exploration resolved onto the battlefield and
/// did nothing at all.
/// </summary>
public sealed class LayerPlayerLimitsTests
{
    private readonly LayerSystem layers = new();

    private static readonly PlayerId Alice = new(0);

    private static GameState Table() =>
        GameReducer.Replay(
        [
            new GameStarted([new PlayerStart("Alice", [], []), new PlayerStart("Bob", [], [])], 7UL, 20, "test"),
        ]);

    private static GameState With(GameState state, ContinuousEffect effect, PlayerId who) =>
        new ContinuousEffectCreated(new ActiveEffect(
            1, effect, null, who, [], [who], new UntilEndOfTurn(), state.NextTimestamp, FromStaticAbility: true))
            .Apply(state);

    [Fact]
    [RuleRef("305.2")]
    public void Nobody_has_an_extra_land_drop_to_begin_with()
    {
        Assert.Equal(0, layers.PlayerRestrictions(Table(), Alice).ExtraLandDrops);
    }

    [Fact]
    [RuleRef("305.2")]
    public void An_extra_land_drops_effect_is_read()
    {
        var state = With(Table(), new ExtraLandDrops(new You(), new Fixed(1)), Alice);

        Assert.Equal(1, layers.PlayerRestrictions(state, Alice).ExtraLandDrops);
    }

    /// <summary>CR 305.2: two such effects let you play two more, not one more.</summary>
    [Fact]
    [RuleRef("305.2")]
    public void Two_of_them_add_up()
    {
        var state = With(Table(), new ExtraLandDrops(new You(), new Fixed(1)), Alice);
        state = new ContinuousEffectCreated(new ActiveEffect(
            2, new ExtraLandDrops(new You(), new Fixed(2)), null, Alice, [], [Alice],
            new UntilEndOfTurn(), state.NextTimestamp, FromStaticAbility: true)).Apply(state);

        Assert.Equal(3, layers.PlayerRestrictions(state, Alice).ExtraLandDrops);
    }

    [Fact]
    [RuleRef("305.2")]
    public void It_belongs_to_the_player_it_applies_to()
    {
        var state = With(Table(), new ExtraLandDrops(new You(), new Fixed(1)), Alice);

        Assert.Equal(0, layers.PlayerRestrictions(state, new PlayerId(1)).ExtraLandDrops);
    }
}
