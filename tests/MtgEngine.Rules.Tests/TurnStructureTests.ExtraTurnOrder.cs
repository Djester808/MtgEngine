using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 500.7's ordering clause: "The most recently created turn will be taken first."
///
/// <see cref="TurnState.ExtraTurns"/> is documented as "most recent first" and
/// <c>TurnSystem.BeginNextTurn</c> takes <c>ExtraTurns[0]</c>, but
/// <see cref="ExtraTurnGranted"/> appended — so the queue was written FIFO and read LIFO and
/// two extra turns created in one window were taken in the order they were created. Nothing
/// caught it because the one existing test seeds <c>ExtraTurns</c> by hand rather than through
/// the event, so the writer and the reader were never exercised together.
/// </summary>
public sealed partial class TurnStructureTests
{
    [Fact]
    [RuleRef("500.7")]
    public void The_most_recently_created_extra_turn_is_taken_first()
    {
        var state = TurnFixture.Table(2).At(Step.End, TurnFixture.P0);

        // P0 creates one, then P1 creates one in response: P1's is the most recent.
        state = new ExtraTurnGranted(TurnFixture.P0).Apply(state);
        state = new ExtraTurnGranted(TurnFixture.P1).Apply(state);

        Assert.Equal(TurnFixture.P1, state.Turn.ExtraTurns[0]);

        var first = Turns.Advance(state);
        Assert.Equal(TurnFixture.P1, Assert.Single(first.Events.OfType<TurnBegan>()).ActivePlayer);

        var second = Turns.Advance(first.State.At(Step.End, TurnFixture.P1, number: 2));
        Assert.Equal(TurnFixture.P0, Assert.Single(second.Events.OfType<TurnBegan>()).ActivePlayer);
    }

    [Fact]
    [RuleRef("500.7")]
    public void Two_extra_turns_for_one_player_are_both_taken()
    {
        var state = TurnFixture.Table(2).At(Step.End, TurnFixture.P0);
        state = new ExtraTurnGranted(TurnFixture.P0).Apply(state);
        state = new ExtraTurnGranted(TurnFixture.P0).Apply(state);

        var first = Turns.Advance(state);
        Assert.Single(first.State.Turn.ExtraTurns);

        var second = Turns.Advance(first.State.At(Step.End, TurnFixture.P0, number: 2));
        Assert.Equal(TurnFixture.P0, Assert.Single(second.Events.OfType<TurnBegan>()).ActivePlayer);
        Assert.Empty(second.State.Turn.ExtraTurns);
    }
}
