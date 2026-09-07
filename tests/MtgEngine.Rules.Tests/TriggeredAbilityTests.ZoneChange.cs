using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Triggers;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 603.6: a zone-change trigger with no destination fires on any arrival, and one that
/// excludes the battlefield as the source is answered from the turn's record of what left it
/// (V11's ZoneChange.NotFrom and optional To; the wave-19 coordinator join).
/// </summary>
public sealed partial class TriggeredAbilityTests
{
    [Fact]
    [RuleRef("603.6")]
    public void A_zone_change_from_anywhere_but_the_battlefield_fires_on_a_discard_and_not_on_a_death()
    {
        var board = new TriggeredAbilityBoard();
        var watcher = board.Put(TriggeredAbilityBoard.Definition(
            "watcher", CardType.Enchantment, [Ability(new ZoneChange(ObjectFilter.Creature, To: Zone.Graveyard, NotFrom: Zone.Battlefield))]));
        var inHand = board.Put(TriggeredAbilityBoard.Creature("held"), zone: Zone.Hand).Object.Id;
        var onBoard = board.Put(TriggeredAbilityBoard.Creature("beast")).Object.Id;

        // A creature card discarded from a hand came from anywhere but the battlefield.
        var discarded = board.Collect(board.MoveTo(inHand, Zone.Graveyard));
        Assert.Equal(watcher.Object.Id, Assert.Single(discarded.Events.OfType<TriggerFired>()).Source);

        // A creature that died came from the battlefield, which the trigger excludes.
        var died = board.Collect(board.Kill(onBoard));
        Assert.Empty(died.Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("603.6")]
    public void A_zone_change_from_a_zone_the_event_cannot_name_is_refused_not_silently_missed()
    {
        var board = new TriggeredAbilityBoard();
        var watcher = board.Put(TriggeredAbilityBoard.Definition(
            "watcher", CardType.Enchantment, [Ability(new ZoneChange(ObjectFilter.Creature, From: Zone.Graveyard))]));
        var buried = board.Put(TriggeredAbilityBoard.Creature("buried"), zone: Zone.Graveyard).Object.Id;

        // "Whenever a creature card leaves your graveyard": the trigger names no destination, and
        // the source zone it names is not knowable from the event today (only the battlefield is,
        // through the turn's record), so the matcher refuses with the rule rather than never firing.
        var ex = Assert.Throws<RefusedException>(() => board.Collect(board.MoveTo(buried, Zone.Exile)));
        Assert.Equal("603.6", ex.Rule);
    }

    [Fact]
    [RuleRef("603.6")]
    public void A_zone_change_excluding_a_zone_the_event_cannot_name_is_refused_not_guessed()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(TriggeredAbilityBoard.Definition(
            "watcher", CardType.Enchantment, [Ability(new ZoneChange(ObjectFilter.Creature, To: Zone.Graveyard, NotFrom: Zone.Hand))]));
        var inHand = board.Put(TriggeredAbilityBoard.Creature("held"), zone: Zone.Hand).Object.Id;

        var ex = Assert.Throws<RefusedException>(() => board.Collect(board.MoveTo(inHand, Zone.Graveyard)));
        Assert.Equal("603.6", ex.Rule);
    }
}
