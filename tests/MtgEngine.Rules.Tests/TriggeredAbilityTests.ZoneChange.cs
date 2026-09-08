using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Triggers;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 603.6: a zone-change trigger with no destination fires on any arrival, and the zone the
/// object left is read from the move itself for every zone — the battlefield through the turn's
/// record (CR 603.10), the rest from the departed object the move carries. A move that never said
/// which zone it left is refused, never guessed.
/// </summary>
public sealed partial class TriggeredAbilityTests
{
    [Fact]
    [RuleRef("603.6")]
    public void A_zone_change_from_a_graveyard_fires_when_a_card_leaves_it_and_not_when_one_dies()
    {
        var board = new TriggeredAbilityBoard();
        var watcher = board.Put(TriggeredAbilityBoard.Definition(
            "watcher", CardType.Enchantment, [Ability(new ZoneChange(ObjectFilter.Creature, From: Zone.Graveyard))]));
        var buried = board.Put(TriggeredAbilityBoard.Creature("buried"), zone: Zone.Graveyard).Object.Id;
        var onBoard = board.Put(TriggeredAbilityBoard.Creature("beast")).Object.Id;

        // "Whenever a creature card leaves your graveyard": the trigger names no destination, so the
        // arrival is not read at all and the card is described where it now lies.
        var left = board.Collect(board.MoveTo(buried, Zone.Exile));
        Assert.Equal(watcher.Object.Id, Assert.Single(left.Events.OfType<TriggerFired>()).Source);

        // A creature that died left the battlefield, not a graveyard, so the same trigger is silent.
        Assert.Empty(board.Collect(board.Kill(onBoard)).Events.OfType<TriggerFired>());
    }

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
    public void A_zone_change_from_a_hand_is_told_apart_from_one_from_a_library()
    {
        var board = new TriggeredAbilityBoard();
        var watcher = board.Put(TriggeredAbilityBoard.Definition(
            "watcher", CardType.Enchantment, [Ability(new ZoneChange(ObjectFilter.Creature, To: Zone.Graveyard, From: Zone.Hand))]));
        var inHand = board.Put(TriggeredAbilityBoard.Creature("held"), zone: Zone.Hand).Object.Id;
        var inLibrary = board.Put(TriggeredAbilityBoard.Creature("deck"), zone: Zone.Library).Object.Id;

        // A milled card reaches the same graveyard from a zone the trigger does not name.
        Assert.Empty(board.Collect(board.MoveTo(inLibrary, Zone.Graveyard)).Events.OfType<TriggerFired>());

        var discarded = board.Collect(board.MoveTo(inHand, Zone.Graveyard));
        Assert.Equal(watcher.Object.Id, Assert.Single(discarded.Events.OfType<TriggerFired>()).Source);
    }

    [Fact]
    [RuleRef("603.6")]
    public void A_zone_change_from_a_move_that_never_said_its_source_zone_is_refused_not_guessed()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(TriggeredAbilityBoard.Definition(
            "watcher", CardType.Enchantment, [Ability(new ZoneChange(ObjectFilter.Creature, To: Zone.Graveyard, NotFrom: Zone.Hand))]));
        var inHand = board.Put(TriggeredAbilityBoard.Creature("held"), zone: Zone.Hand).Object.Id;

        // The zone system's moves carry the object that left, so this is a move no subsystem makes;
        // the matcher still refuses it with the rule rather than answering the exclusion by guess.
        var ex = Assert.Throws<RefusedException>(() => board.Collect(board.MoveTo(inHand, Zone.Graveyard, carryDeparted: false)));
        Assert.Equal("603.6", ex.Rule);
    }

    [Fact]
    [RuleRef("603.6")]
    public void A_zone_change_that_names_no_source_zone_reads_a_move_that_recorded_none()
    {
        var board = new TriggeredAbilityBoard();
        var watcher = board.Put(TriggeredAbilityBoard.Definition(
            "watcher", CardType.Enchantment, [Ability(new ZoneChange(ObjectFilter.Creature, To: Zone.Graveyard))]));
        var inHand = board.Put(TriggeredAbilityBoard.Creature("held"), zone: Zone.Hand).Object.Id;

        // The refusal is scoped to a trigger that asks where the object came from: one that asks
        // only where it arrived reads the destination, which is always on the event.
        var arrived = board.Collect(board.MoveTo(inHand, Zone.Graveyard, carryDeparted: false));
        Assert.Equal(watcher.Object.Id, Assert.Single(arrived.Events.OfType<TriggerFired>()).Source);
    }
}
