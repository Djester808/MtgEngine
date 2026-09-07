using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// "This ability triggers only once each turn", CR 603.2h. The restriction is about triggering, so
/// a firing counts from the moment the ability goes on the stack and not only once it resolves;
/// these tests drive both halves of that reading — the stack object waiting, and the turn's
/// resolution tally after it has gone — and the turn boundary that clears them.
/// </summary>
public sealed class OncePerTurnTests
{
    private static readonly Effect Gain = new GainLife(new Fixed(1));

    private static CardDefinition Watcher(bool oncePerTurn) =>
        TriggeredAbilityBoard.Definition(
            "watcher",
            CardType.Enchantment,
            [new TriggeredAbility(new EntersBattlefield(ObjectFilter.Creature), Gain, OncePerTurn: oncePerTurn)]);

    [Fact]
    [RuleRef("603.2h")]
    public void ItFiresForTheFirstEventAndNotForASecondOneInTheSameTurn()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(Watcher(oncePerTurn: true));

        var first = board.Collect(board.Put(TriggeredAbilityBoard.Creature("one")));
        Assert.Single(first.Events.OfType<TriggerFired>());
        board.Fold([.. first.Events]);

        // The ability is on the stack and has not resolved; the rule is about triggering, so it
        // has already triggered this turn and the second creature entering does nothing.
        var second = board.Collect(board.Put(TriggeredAbilityBoard.Creature("two")));
        Assert.Empty(second.Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("603.2h")]
    public void TwoEventsInOneBatchFireItOnce()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(Watcher(oncePerTurn: true));

        var one = board.Put(TriggeredAbilityBoard.Creature("one"));
        var two = board.Put(TriggeredAbilityBoard.Creature("two"));

        // Neither firing is on the stack while the batch is being collected, so the batch itself
        // has to remember that the ability has already fired.
        var fired = Assert.Single(board.Collect(one, two).Events.OfType<TriggerFired>());
        Assert.Equal(one.Object.Id, Assert.Single(fired.Subjects));
    }

    [Fact]
    [RuleRef("603.2h")]
    public void AResolutionThisTurnIsAlsoAFiringEvenAfterTheStackObjectHasGone()
    {
        var board = new TriggeredAbilityBoard();
        var watcher = board.Put(Watcher(oncePerTurn: true)).Object.Id;

        Resolve(board, board.Put(TriggeredAbilityBoard.Creature("one")));
        Assert.Equal(1, board.State.History.AbilitiesResolvedThisTurn.GetValueOrDefault((watcher, 0)));
        Assert.Empty(board.State.In(ZoneKey.Shared(Zone.Stack)));

        var second = board.Collect(board.Put(TriggeredAbilityBoard.Creature("two")));
        Assert.Empty(second.Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("500.1")]
    public void ItFiresAgainOnTheNextTurn()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(Watcher(oncePerTurn: true));

        Resolve(board, board.Put(TriggeredAbilityBoard.Creature("one")));
        Assert.Empty(board.Collect(board.Put(TriggeredAbilityBoard.Creature("two"))).Events.OfType<TriggerFired>());

        // CR 500.1: a turn begins and the turn's history — the tally with it — starts empty.
        board.Fold(new TurnBegan(2, new PlayerId(0), false));

        var again = board.Collect(board.Put(TriggeredAbilityBoard.Creature("three")));
        Assert.Single(again.Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("603.2")]
    public void AnAbilityWithoutTheRestrictionFiresForEveryEvent()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(Watcher(oncePerTurn: false));

        var first = board.Collect(board.Put(TriggeredAbilityBoard.Creature("one")));
        Assert.Single(first.Events.OfType<TriggerFired>());
        board.Fold([.. first.Events]);

        var second = board.Collect(board.Put(TriggeredAbilityBoard.Creature("two")));
        Assert.Single(second.Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("603.2h")]
    public void OneSourcesRestrictionDoesNotSilenceAnothersCopyOfTheSameAbility()
    {
        var board = new TriggeredAbilityBoard();
        board.Put(Watcher(oncePerTurn: true));
        board.Put(Watcher(oncePerTurn: true));

        // Two objects, so two abilities: the tally and the stack are keyed on the source, CR 603.2.
        var fired = board.Collect(board.Put(TriggeredAbilityBoard.Creature("one"))).Events.OfType<TriggerFired>();
        Assert.Equal(2, fired.Count());
    }

    /// <summary>
    /// Fires the trigger on <paramref name="cause"/>, then resolves the stack object it made and
    /// takes it off the stack, the way CR 608.2m leaves the ability with nothing on the stack.
    /// </summary>
    private static void Resolve(TriggeredAbilityBoard board, GameEvent cause)
    {
        board.Fold([.. board.Collect(cause).Events]);
        foreach (var onStack in board.State.In(ZoneKey.Shared(Zone.Stack)))
            board.Fold(new StackObjectResolved(onStack), new ObjectCeasedToExist(onStack));
    }
}
