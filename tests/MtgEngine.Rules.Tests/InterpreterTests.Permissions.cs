using MtgEngine.Cards;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 601.3a — "you may play it": the vocabulary's <see cref="MayPlay"/> is a lasting permission,
/// the same continuous effect a may-cast with a duration creates, because a land is played
/// through the land-play door (CR 305.1) and a spell through a cast its player announces.
/// </summary>
public sealed partial class InterpreterTests
{
    [Fact]
    [RuleRef("601.3a")]
    public void A_may_play_is_a_lasting_permission_that_carries_the_free_half_and_the_zone()
    {
        var table = new InterpreterTable();
        var card = table.Card(table.A, Zone.Exile);

        table.Resolve(table.OnStack(
            table.A,
            new MayPlay(new Remembered("it"), WithoutPayingManaCost: true),
            Remembering(card)));

        // CR 118.9 with 305.1: "you may play it without paying its mana cost" is one permission for
        // a card that may be a land — so it is the play permission the casting system reads, from
        // the zone the card is in now, with the free half on it; nothing was played on the spot.
        var active = Assert.Single(table.State.ActiveEffects);
        var permission = Assert.IsType<MayPlayFromZone>(active.Effect);
        Assert.True(permission.WithoutPayingManaCost);
        Assert.Equal(Zone.Exile, permission.From);
        Assert.Equal([card], active.AppliesTo);
        Assert.Equal(Zone.Exile, table.State.Objects[card].Zone.Zone);

        // CR 611.2a: the card names no duration, so the permission lasts until end of turn.
        Assert.IsType<UntilEndOfTurn>(active.Until);
    }

    [Fact]
    [RuleRef("601.3a")]
    public void A_may_play_that_names_nothing_creates_no_permission()
    {
        var table = new InterpreterTable();

        table.Resolve(table.OnStack(table.A, new MayPlay(new Remembered("nothing"))));

        Assert.Empty(table.State.ActiveEffects);
    }
}
