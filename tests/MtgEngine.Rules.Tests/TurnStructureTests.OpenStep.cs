using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 117.3b: opening a step that something finished out of band. An unhalted resolution and an
/// unhalted turn-based action grant priority inside the call that did them; a resolution or a
/// combat damage batch a question stopped comes back through the answer instead, and the choice
/// system asks for the step here rather than spelling the grant a second time
/// (<c>seam-requests/E4e.md</c>). It is asked after every card's answer, so most of what it is
/// asked to do is nothing, and each of those refusals is a rule.
/// </summary>
public sealed partial class TurnStructureTests
{
    [Fact]
    [RuleRef("117.3b")]
    public void Open_step_gives_the_active_player_priority_in_the_step_they_are_in()
    {
        var state = TurnFixture.Table(2).At(Step.CombatDamage, TurnFixture.P0);

        var outcome = Turns.OpenStep(state);

        Assert.Equal(TurnFixture.P0, Assert.Single(outcome.Events.OfType<PriorityGranted>()).Player);
        Assert.Equal(TurnFixture.P0, outcome.State.Priority.Holder);

        // CR 117.3b opens the step that is already running: nothing here advances the turn.
        Assert.Equal(Step.CombatDamage, outcome.State.Turn.Step);
    }

    [Fact]
    [RuleRef("117.3b")]
    public void Open_step_is_a_fresh_round_of_priority()
    {
        // CR 117.4: the passes that resolved the thing being finished are spent; the round that
        // opens now is nobody having passed yet.
        var state = TurnFixture.Table(2).At(Step.PrecombatMain, TurnFixture.P0) with
        {
            Priority = new PriorityState(null, [TurnFixture.P0, TurnFixture.P1]),
        };

        var outcome = Turns.OpenStep(state);

        Assert.Equal(TurnFixture.P0, outcome.State.Priority.Holder);
        Assert.Empty(outcome.State.Priority.PassedInSuccession);
    }

    [Fact]
    [RuleRef("104.3")]
    public void Open_step_walks_past_an_active_player_who_has_lost()
    {
        // CR 104.3: a player who has lost is no longer in the game, so priority goes on to the
        // next player in turn order — the same walk the ordinary grant makes.
        var state = TurnFixture.Table(3).At(Step.CombatDamage, TurnFixture.P0);
        state = new PlayerLost(TurnFixture.P0, "test").Apply(state);

        var outcome = Turns.OpenStep(state);

        Assert.Equal(TurnFixture.P1, outcome.State.Priority.Holder);
    }

    [Fact]
    [RuleRef("101.4")]
    public void Open_step_grants_nothing_while_another_question_stands()
    {
        // CR 101.4: the answer raised a second question, so the game is waiting again and the step
        // is not finished with. This is what makes a twice-stopped resolution open the step once.
        var state = new ChoiceRequested(Question()).Apply(TurnFixture.Table(2).At(Step.CombatDamage, TurnFixture.P0));

        var outcome = Turns.OpenStep(state);

        Assert.Empty(outcome.Events);
        Assert.Null(outcome.State.Priority.Holder);
    }

    [Fact]
    [RuleRef("117.3")]
    public void Open_step_grants_nothing_to_a_step_a_player_already_holds()
    {
        // CR 117.3: one player holds priority at a time. A question asked during a cast is
        // answered by the player who has it, and the answer does not take it off them.
        var state = TurnFixture.Table(2).At(Step.PrecombatMain, TurnFixture.P0).Holding(TurnFixture.P1);

        var outcome = Turns.OpenStep(state);

        Assert.Empty(outcome.Events);
        Assert.Equal(TurnFixture.P1, outcome.State.Priority.Holder);
    }

    [Fact]
    [RuleRef("104.1")]
    public void Open_step_grants_nothing_once_the_game_has_ended()
    {
        var state = TurnFixture.Table(2).At(Step.CombatDamage, TurnFixture.P0);
        state = new GameEnded(new GameOutcome([TurnFixture.P0], false, "test")).Apply(state);

        var outcome = Turns.OpenStep(state);

        Assert.Empty(outcome.Events);
        Assert.Null(outcome.State.Priority.Holder);
    }

    /// <summary>Any question at all: what it asks is not this test's business, only that it stands.</summary>
    private static PendingChoice Question() =>
        new(
            new ChoiceId(1),
            TurnFixture.P0,
            "test",
            "answer something",
            [],
            0,
            0,
            new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty));
}
