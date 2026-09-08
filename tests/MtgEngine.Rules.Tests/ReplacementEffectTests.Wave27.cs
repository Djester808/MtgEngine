using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 27, CR 614.1a with 400.7 and 101.4: a question raised by the <c>With</c> of an "instead"
/// on an entering permanent has to travel under an id the state it comes back to still holds.
/// Every definition here is invented; no card name reaches the engine or its tests.
/// </summary>
public sealed partial class ReplacementEffectTests
{
    /// <summary>The key the may-pay's cost is a stand-in for; a card's key, not the engine's.</summary>
    private const string Wave27Key = "w27";

    /// <summary>
    /// CR 614.1a with 400.7: the object that would have entered exists in the probe state alone, and
    /// an "instead" means it never arrives. The walk therefore runs under the card as it still is —
    /// the one in the zone it was leaving — so the answer to a question it raises can find the effect
    /// that asked again (CR 101.4). Under the entering id nothing in the state carries that id and
    /// the resumption is refused.
    /// </summary>
    [Fact]
    public void An_instead_on_an_entering_permanent_runs_under_the_card_that_is_leaving()
    {
        var board = new Board();
        var card = board.Put(
            WithReplacement("wave27 hesitant relic", CardType.Artifact, new WouldEnterBattlefield(new ObjectFilter(Types: CardType.Artifact)), Wave27MayPay, self: true),
            Zone.Hand);
        var entering = Entering(board.State, card);

        var outcome = System().Replace(board.State, entering, new PlayerId(0));

        var context = Assert.Single(_interpreter.Contexts);
        Assert.Equal(card, context.Source);
        Assert.NotEqual(entering.To.Id, context.Source);
        Assert.True(outcome.State.Objects.ContainsKey(context.Source!.Value));
    }

    /// <summary>
    /// CR 101.4: the ability index travels with the source it belongs to, so it is that card's own
    /// face the answer counts abilities on. The replacement is deliberately the second ability, so a
    /// defaulted zero would fail this.
    /// </summary>
    [Fact]
    public void The_index_an_instead_travels_with_is_read_on_the_leaving_card()
    {
        var board = new Board();
        var card = board.Put(
            Board.Definition(
                "wave27 second-ability relic",
                CardType.Artifact,
                abilities:
                [
                    new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{C}"))),
                    new ReplacementAbility(new WouldEnterBattlefield(new ObjectFilter(Types: CardType.Artifact)), Wave27MayPay, SelfOnly: true),
                ]),
            Zone.Hand);

        System().Replace(board.State, Entering(board.State, card), new PlayerId(0));

        Assert.Equal(1, Assert.Single(_interpreter.Indexes));
    }

    /// <summary>
    /// CR 614.1c: the other road is untouched. A choose-only <c>With</c> is part of the entering, the
    /// object does arrive, and the question keeps the entering id the answer will write CR 608.2d's
    /// value on (E8d) — swapping it for the leaving card would break that resumption instead.
    /// </summary>
    [Fact]
    public void A_choose_only_with_still_runs_under_the_object_that_arrives()
    {
        var board = new Board();
        var card = board.Put(
            WithReplacement("wave27 deep hollow", CardType.Land, new WouldEnterBattlefield(Land), new ChooseValue(ChooseWhat.CreatureType, Wave27Key), self: true),
            Zone.Hand);
        var entering = Entering(board.State, card);

        var outcome = System().Replace(board.State, entering, new PlayerId(0));

        var context = Assert.Single(_interpreter.Contexts);
        Assert.Equal(entering.To.Id, context.Source);
        Assert.True(outcome.State.Objects.ContainsKey(entering.To.Id));
    }

    /// <summary>
    /// CR 614.1a: an event whose source is an object that is not going anywhere is handed over under
    /// its own id, exactly as before — the swap is only for an object that exists in the probe alone.
    /// </summary>
    [Fact]
    public void An_instead_raised_by_a_permanent_that_is_not_moving_keeps_its_own_source()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(2, 2, "wave27 doomed thing"));
        var watcher = board.Put(
            WithReplacement("wave27 shroud", CardType.Enchantment, new WouldDie(ObjectFilter.Creature), new ReplaceWith(new Exile(new Self()))));

        System().Replace(board.State, Dying(board.State, creature), new PlayerId(0));

        Assert.Equal(watcher, Assert.Single(_interpreter.Contexts).Source);
    }

    /// <summary>"You may discard a land card" — the shape CR 614.1a's "instead" halts on.</summary>
    private static MayPay Wave27MayPay { get; } = new(
        new DiscardCost(What: new ObjectFilter(Types: CardType.Land)),
        new Move(new Self(), Zone.Battlefield),
        new Move(new Self(), Zone.Graveyard));
}
