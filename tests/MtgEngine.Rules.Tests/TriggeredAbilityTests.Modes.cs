using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 603.3c with 700.2b: a modal triggered ability's modes are chosen as it is put on the stack,
/// so a ceiling that depends on the game is read there, once, and rides on the stack entry for the
/// resolution's question to offer (E3l's ask 2).
/// </summary>
public sealed partial class TriggeredAbilityTests
{
    /// <summary>"Choose one — … . If it's your turn, choose two instead."</summary>
    private static TriggeredAbility Modal(int max, int whenItIsYourTurn) =>
        new(
            new EntersBattlefield(ObjectFilter.Creature),
            new ChooseMode(
                [new Mode("gain 1", new GainLife(new Fixed(1))), new Mode("gain 2", new GainLife(new Fixed(2)))],
                Min: 1,
                Max: max,
                MaxIf: new IsYourTurn(),
                MaxIfMet: whenItIsYourTurn));

    private static StackEntry Announced(Outcome outcome) =>
        Assert.Single(outcome.Events.OfType<ObjectCreated>(), e => e.Object.Stack is not null).Object.Stack!;

    [Fact]
    [RuleRef("603.3c")]
    public void A_modal_triggers_ceiling_is_read_as_it_goes_on_the_stack_when_its_condition_holds()
    {
        var board = new TriggeredAbilityBoard();
        board.ActivePlayer(0);
        board.Put(TriggeredAbilityBoard.Definition("watcher", CardType.Enchantment, [Modal(max: 1, whenItIsYourTurn: 2)]));

        var outcome = board.Collect(board.Put(TriggeredAbilityBoard.Creature("beast")));

        var chosen = Announced(outcome).Chosen;
        Assert.NotNull(chosen);
        Assert.Equal("2", chosen[ModeAnnouncement.CeilingKey]);
    }

    [Fact]
    [RuleRef("700.2")]
    public void A_modal_triggers_ceiling_is_the_printed_one_when_its_condition_does_not_hold()
    {
        var board = new TriggeredAbilityBoard();
        board.ActivePlayer(1);
        board.Put(TriggeredAbilityBoard.Definition("watcher", CardType.Enchantment, [Modal(max: 1, whenItIsYourTurn: 2)]));

        // The watcher's controller is seat 0, so it is not their turn and the printed Max stands.
        var outcome = board.Collect(board.Put(TriggeredAbilityBoard.Creature("beast")));

        var chosen = Announced(outcome).Chosen;
        Assert.NotNull(chosen);
        Assert.Equal("1", chosen[ModeAnnouncement.CeilingKey]);
    }

    [Fact]
    [RuleRef("603.3c")]
    public void An_ordinary_trigger_records_no_ceiling_at_all()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(TriggeredAbilityBoard.Definition(
            "watcher", CardType.Enchantment, [Ability(new EntersBattlefield(ObjectFilter.Creature))]));

        var outcome = board.Collect(board.Put(TriggeredAbilityBoard.Creature("beast")));

        Assert.Null(Announced(outcome).Chosen);
    }
}
