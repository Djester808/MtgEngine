using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Turn;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Timing and priority, CR 117: who holds it, where it goes when they pass, and what happens
/// when everyone has passed in succession — the top of the stack resolves, or the step ends.
/// </summary>
public sealed class PriorityTests
{
    private readonly TurnFixture.FakeZones _zones = new();
    private readonly TurnFixture.FakeCasting _casting = new();

    private TurnSystem Turns => new(_zones, _casting);

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    public void Losing_priority_holder_passes_to_next_survivor_and_the_turn_continues(int seat, bool skipNext)
    {
        var player = new PlayerId(seat);
        var initial = TurnFixture.Table(4).At(Step.PrecombatMain, player).Holding(player);
        var events = new List<GameEvent>();
        if (skipNext)
            events.Add(new PlayerLost(new PlayerId((seat + 1) % 4), "earlier loss"));
        events.Add(new PlayerLost(player, "drew from empty library"));
        var state = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        var next = new PlayerId((seat + (skipNext ? 2 : 1)) % 4);
        Assert.Equal(next, state.Priority.Holder);
        Assert.Throws<RefusedException>(() => Turns.Pass(state, player));
        for (var index = 0; index < (skipNext ? 2 : 3); index++)
            state = Turns.Pass(state, state.Priority.Holder!.Value).State;
        Assert.Equal(Step.BeginningOfCombat, state.Turn.Step);
        Assert.Equal(player, state.Turn.ActivePlayer);
        Assert.Equal(next, state.Priority.Holder);
    }

    [Fact]
    public void The_active_player_receives_priority_when_a_step_begins()
    {
        // CR 117.3a.
        var outcome = Turns.Advance(TurnFixture.Table(2).At(Step.Upkeep, TurnFixture.P0));

        Assert.Equal(TurnFixture.P0, Assert.Single(outcome.Events.OfType<PriorityGranted>()).Player);
        Assert.Equal(TurnFixture.P0, outcome.State.Priority.Holder);
    }

    [Fact]
    public void No_player_receives_priority_during_the_untap_step()
    {
        // CR 117.3a and CR 502.4: the first grant of a turn is in the upkeep step.
        var log = Turns.Begin(TurnFixture.Table(2)).Events;

        var upkeep = log.FindIndex(e => e is StepBegan { Step: Step.Upkeep });
        var granted = log.FindIndex(e => e is PriorityGranted);
        Assert.True(upkeep >= 0 && granted > upkeep, "priority was granted before the upkeep step began");
        Assert.Single(log.OfType<PriorityGranted>());
    }

    [Fact]
    public void A_pass_from_a_player_who_does_not_hold_priority_is_refused()
    {
        // CR 117.3: one player holds priority at a time.
        var state = TurnFixture.Table(2).At(Step.PrecombatMain, TurnFixture.P0).Holding(TurnFixture.P0);

        var refused = Assert.Throws<RefusedException>(() => Turns.Pass(state, TurnFixture.P1));

        Assert.Equal("117.3", refused.Rule);
    }

    [Fact]
    public void A_pass_hands_priority_to_the_next_player_in_turn_order()
    {
        // CR 117.3d.
        var state = TurnFixture.Table(2).At(Step.PrecombatMain, TurnFixture.P0).Holding(TurnFixture.P0);

        var outcome = Turns.Pass(state, TurnFixture.P0);

        Assert.Equal(TurnFixture.P0, Assert.Single(outcome.Events.OfType<PriorityPassed>()).Player);
        Assert.Equal(TurnFixture.P1, outcome.State.Priority.Holder);
        Assert.Equal(Step.PrecombatMain, outcome.State.Turn.Step);
    }

    [Fact]
    public void Passing_in_succession_with_an_empty_stack_ends_the_step()
    {
        // CR 117.4 and CR 500.2.
        var state = TurnFixture.Table(2).At(Step.PrecombatMain, TurnFixture.P0).Holding(TurnFixture.P0);

        state = Turns.Pass(state, TurnFixture.P0).State;
        var outcome = Turns.Pass(state, TurnFixture.P1);

        Assert.Equal(Step.BeginningOfCombat, Assert.Single(outcome.Events.OfType<StepBegan>()).Step);
        Assert.Equal(Step.BeginningOfCombat, outcome.State.Turn.Step);
        Assert.Equal(TurnFixture.P0, outcome.State.Priority.Holder);
    }

    [Fact]
    public void All_four_players_have_to_pass_before_the_step_ends()
    {
        // CR 117.4: in succession means every player still in the game.
        var state = TurnFixture.Table(4).At(Step.PrecombatMain, TurnFixture.P0).Holding(TurnFixture.P0);

        var order = new List<PlayerId>();
        for (var i = 0; i < 3; i++)
        {
            var outcome = Turns.Pass(state, state.Priority.Holder!.Value);
            state = outcome.State;
            order.Add(state.Priority.Holder!.Value);
            Assert.Equal(Step.PrecombatMain, state.Turn.Step);
        }

        var last = Turns.Pass(state, state.Priority.Holder!.Value);

        Assert.Equal(new[] { new PlayerId(1), new PlayerId(2), new PlayerId(3) }, order);
        Assert.Equal(Step.BeginningOfCombat, last.State.Turn.Step);
        Assert.Equal(TurnFixture.P0, last.State.Priority.Holder);
    }

    [Fact]
    public void A_player_who_has_lost_is_neither_given_priority_nor_waited_for()
    {
        // CR 104.3 with CR 117.4.
        var state = TurnFixture.Table(3).At(Step.PrecombatMain, TurnFixture.P0).Holding(TurnFixture.P0);
        state = new PlayerLost(TurnFixture.P1, "test").Apply(state);

        var handed = Turns.Pass(state, TurnFixture.P0);
        Assert.Equal(TurnFixture.P2, handed.State.Priority.Holder);

        var outcome = Turns.Pass(handed.State, TurnFixture.P2);

        Assert.Equal(Step.BeginningOfCombat, outcome.State.Turn.Step);
        Assert.Equal(TurnFixture.P0, outcome.State.Priority.Holder);
    }

    [Fact]
    public void Passing_in_succession_with_a_stack_resolves_its_top_object()
    {
        // CR 117.4 and CR 608.2, and CR 117.3b for who gets priority afterwards.
        var state = TurnFixture.Table(2).At(Step.PrecombatMain, TurnFixture.P1, number: 2)
            .With(TurnFixture.OnStack(1, TurnFixture.P0))
            .Holding(TurnFixture.P1);

        state = Turns.Pass(state, TurnFixture.P1).State;
        var outcome = Turns.Pass(state, TurnFixture.P0);

        Assert.Equal(new ObjectId(1), Assert.Single(_casting.Resolved));
        Assert.Empty(outcome.Events.OfType<StepBegan>());
        Assert.Equal(Step.PrecombatMain, outcome.State.Turn.Step);

        // CR 117.3b: the active player receives priority after something resolves.
        Assert.Equal(TurnFixture.P1, outcome.State.Priority.Holder);
        Assert.Empty(outcome.State.Priority.PassedInSuccession);
    }

    [Fact]
    public void Priority_returns_to_the_active_player_after_each_resolution()
    {
        // Two objects on the stack: the second round of passes resolves the second one, and
        // the step still has not ended, CR 117.4.
        var state = TurnFixture.Table(2).At(Step.PrecombatMain, TurnFixture.P0)
            .With(TurnFixture.OnStack(1, TurnFixture.P0), TurnFixture.OnStack(2, TurnFixture.P1))
            .Holding(TurnFixture.P0);

        for (var i = 0; i < 4; i++)
            state = Turns.Pass(state, state.Priority.Holder!.Value).State;

        Assert.Equal(new[] { new ObjectId(2), new ObjectId(1) }, _casting.Resolved);
        Assert.Equal(Step.PrecombatMain, state.Turn.Step);
        Assert.Equal(TurnFixture.P0, state.Priority.Holder);
    }
}
