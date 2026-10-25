using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Turn;
using MtgEngine.Rules.Views;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "TurnPriority")]
public sealed class TurnPriorityContractTests
{
    private static TurnSystem Turns => new(new TurnFixture.FakeZones(), new TurnFixture.FakeCasting());
    private static GameState Table => TurnFixture.Table(4).At(Step.PrecombatMain, new PlayerId(0)).Holding(new PlayerId(1));
    private static PendingChoice Choice => new(new ChoiceId(1), new PlayerId(2), "test", "Choose", [], 0, 0,
        new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty));

    [Fact]
    public void Turn_priority_and_choice_are_three_distinct_identities()
    {
        var state = Table with { Pending = Choice };
        Assert.Equal(new PlayerId(0), new TurnSnapshot(state).ActivePlayer);
        var priority = new PrioritySnapshot(state);
        Assert.Equal(new PlayerId(1), priority.Holder);
        Assert.Equal(new PlayerId(2), priority.PendingChooser);
        Assert.False(priority.IsAvailable);
        Assert.False(priority.CanPass(new PlayerId(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Direct_pass_is_refused_while_a_choice_or_game_end_blocks_priority(bool ended)
    {
        var state = ended ? Table with { Outcome = new GameOutcome([], true, "test") } : Table with { Pending = Choice };
        var before = state.Priority;
        Assert.Throws<RefusedException>(() => Turns.Pass(state, new PlayerId(1)));
        Assert.Same(before, state.Priority);
    }

    [Fact]
    public void Setup_has_turn_zero_and_no_priority()
    {
        var state = TurnFixture.Table(4);
        Assert.Equal(0, new TurnSnapshot(state).Number);
        Assert.Null(new PrioritySnapshot(state).Holder);
        Assert.False(new PrioritySnapshot(state).IsAvailable);
    }

    [Fact]
    public void Captured_snapshots_survive_a_pass_and_serialized_replay()
    {
        var state = Table;
        var turn = new TurnSnapshot(state);
        var priority = new PrioritySnapshot(state);
        var outcome = Turns.Pass(state, new PlayerId(1));
        var replay = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(outcome.Events)));
        Assert.Equal(new PlayerId(1), priority.Holder);
        Assert.Empty(priority.PassedInSuccession);
        Assert.True(priority.CanPass(new PlayerId(1)));
        Assert.Equal(turn.ActivePlayer, replay.Turn.ActivePlayer);
        Assert.Equal(turn.Step, replay.Turn.Step);
        Assert.Equal(new PlayerId(2), new PrioritySnapshot(replay).Holder);
        Assert.Equal(new[] { new PlayerId(1) }, new PrioritySnapshot(replay).PassedInSuccession);
    }

    [Fact]
    public void Step_entry_clears_priority_and_grant_starts_a_fresh_round()
    {
        var state = new PriorityPassed(new PlayerId(1)).Apply(Table);
        state = new StepBegan(Step.PostcombatMain).Apply(state);
        Assert.Null(new PrioritySnapshot(state).Holder);
        Assert.Empty(new PrioritySnapshot(state).PassedInSuccession);
        state = new PriorityGranted(state.Turn.ActivePlayer).Apply(state);
        Assert.True(new PrioritySnapshot(state).CanPass(new TurnSnapshot(state).ActivePlayer));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void Snapshots_reject_out_of_table_player_references(int seat)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnSnapshot(Table with { Turn = Table.Turn with { ActivePlayer = new PlayerId(seat) } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrioritySnapshot(Table with { Priority = new PriorityState(new PlayerId(seat), []) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrioritySnapshot(Table with { Pending = Choice with { Player = new PlayerId(seat) } }));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(999)]
    [InlineData((int)Step.Main)]
    public void A_turn_requires_a_concrete_step(int step)
    {
        Assert.Throws<InvalidOperationException>(() => new TurnSnapshot(Table with { Turn = Table.Turn with { Step = (Step)step } }));
    }

    [Fact]
    public void A_departed_active_player_is_still_the_turn_owner_but_cannot_use_priority()
    {
        var state = Table with { Players = Table.Players.SetItem(0, Table.Player(new PlayerId(0)) with { HasLost = true }) };
        Assert.Equal(new PlayerId(0), new TurnSnapshot(state).ActivePlayer);
        state = state.Holding(new PlayerId(0));
        Assert.False(new PrioritySnapshot(state).IsAvailable);
    }

    [Fact]
    public void Every_view_keeps_the_three_identities_separate_and_redacts_choice_details()
    {
        var state = Table with { Pending = Choice with { Options = [new ChoiceOption("private", "Private option")] } };
        var projector = new ViewProjector(new LayerSystem(), _ => true);
        foreach (var player in state.Players)
        {
            var view = projector.Project(state, player.Id);
            Assert.Equal(new TurnSnapshot(state).ActivePlayer, view.ActivePlayer);
            Assert.Equal(new PrioritySnapshot(state).Holder, view.PriorityHolder);
            Assert.Equal(new PlayerId(2), view.Pending!.Player);
            Assert.Equal(player.Id == new PlayerId(2) ? 1 : 0, view.Pending.Options.Count);
        }
    }

    [Fact]
    public void Invalid_turn_number_order_and_pass_history_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnSnapshot(Table with { Turn = Table.Turn with { Number = -1 } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnSnapshot(Table with { Turn = Table.Turn with { TurnOrderIndex = 4 } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrioritySnapshot(Table with { Priority = new PriorityState(null, [new PlayerId(4)]) }));
    }
}
