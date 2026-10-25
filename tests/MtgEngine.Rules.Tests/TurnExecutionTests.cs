using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

public sealed class TurnExecutionTests
{
    private static GameState Setup(int seats = 2) => GameSetup.Create(
        Enumerable.Range(0, seats).Select(i => PlayerState.Create(new(i), "Seat", 20)));

    // Deliberately explicit fixtures for a world without SBAs/triggers or turn-action effects.
    // Production callers must supply real providers for their supported rules subset.
    private static GameState Identity(GameState state) => state;
    private static TurnExecution Ready(int seats = 2, int starter = 0) =>
        TurnExecution.Start(Setup(seats), new(starter)).CompleteTurnAction(Identity).CompleteCheckpoint(Identity);

    private static TurnExecution PassRound(TurnExecution execution)
    {
        for (var i = 0; i < execution.State.Players.Count; i++)
        {
            execution = execution.Pass(execution.State.Priority.Holder!.Value);
            if (i < execution.State.Players.Count - 1)
                execution = execution.CompleteCheckpoint(Identity);
        }
        return execution;
    }

    private static TurnExecution Cleanup(int seats = 2)
    {
        var execution = Ready(seats);
        while (execution.State.Turn.Step != Step.Cleanup)
        {
            execution = PassRound(execution);
            if (execution.Boundary == ExecutionBoundary.TurnAction)
            {
                if (execution.State.Turn.Step == Step.Cleanup)
                    break;
                execution = execution.CompleteTurnAction(s => s.Turn.Step == Step.DeclareAttackers
                    ? s with
                    {
                        Combat = new(s.Turn.ActivePlayer, ImmutableDictionary<ObjectId, CombatTarget>.Empty,
                        ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty, true, false)
                    } : s);
            }
            execution = execution.CompleteCheckpoint(Identity);
        }
        return execution;
    }

    [Fact]
    public void Untap_has_no_priority_and_upkeep_requires_a_checkpoint_CR502_4()
    {
        var execution = TurnExecution.Start(Setup(), new(1));
        Assert.Equal(Step.Untap, execution.State.Turn.Step);
        Assert.Null(execution.State.Priority.Holder);
        Assert.Throws<InvalidOperationException>(() => execution.Pass(new(1)));
        execution = execution.CompleteTurnAction(Identity);
        Assert.Equal(Step.Upkeep, execution.State.Turn.Step);
        Assert.Equal(ExecutionBoundary.Checkpoint, execution.Boundary);
        Assert.Null(execution.State.Priority.Holder);
        execution = execution.CompleteCheckpoint(Identity);
        Assert.Equal(new PlayerId(1), execution.State.Priority.Holder);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Passes_visit_every_seat_in_turn_order_before_advancing_CR117_3d(int seats)
    {
        var execution = Ready(seats, seats - 1);
        for (var i = 0; i < seats; i++)
        {
            var actor = new PlayerId((seats - 1 + i) % seats);
            Assert.Equal(actor, execution.State.Priority.Holder);
            Assert.Equal(new PlayerId(seats - 1), execution.State.Turn.ActivePlayer);
            execution = execution.Pass(actor);
            if (i < seats - 1)
            {
                Assert.Equal(Step.Upkeep, execution.State.Turn.Step);
                execution = execution.CompleteCheckpoint(Identity);
            }
        }
        Assert.Equal(seats == 2 ? Step.PrecombatMain : Step.Draw, execution.State.Turn.Step);
    }

    [Fact]
    public void Wrong_actor_cannot_pass_or_invoke_action_provider()
    {
        var execution = Ready();
        Assert.Throws<InvalidOperationException>(() => execution.Pass(new(1)));
        var invoked = false;
        Assert.Throws<InvalidOperationException>(() => execution.Act(new(1), s => { invoked = true; return s; }));
        Assert.False(invoked);
    }

    [Fact]
    public void An_intervening_action_resets_passes_and_returns_priority_to_actor_CR117_3c()
    {
        var execution = Ready(3).Pass(new(0)).CompleteCheckpoint(Identity);
        execution = execution.Act(new(1), s => s with
        {
            Players = s.Players.SetItem(1, s.Players[1] with { Life = 19 }),
        });
        Assert.Empty(execution.State.Priority.PassedInSuccession);
        execution = execution.CompleteCheckpoint(Identity);
        Assert.Equal(new PlayerId(1), execution.State.Priority.Holder);
        execution = execution.Pass(new(1)).CompleteCheckpoint(Identity).Pass(new(2)).CompleteCheckpoint(Identity);
        Assert.Equal(Step.Upkeep, execution.State.Turn.Step);
        Assert.Equal(new PlayerId(0), execution.State.Priority.Holder);
        Assert.Equal(Step.Draw, execution.Pass(new(0)).State.Turn.Step);
    }

    private static GameState AddSpell(GameState state)
    {
        var definition = new CardDefinition("spell", "Spell", Layout.Normal,
            [new Face("Spell", FaceRole.Main, new TypeLine(CardType.Instant))]);
        state = GameSetup.AddCard(state, new(0), definition, "spell");
        return ZoneTransitions.Move(state, new(new(state.LastObjectId), ZoneKey.Shared(Zone.Stack))).State;
    }

    [Fact]
    public void Stack_resolves_one_top_at_a_time_and_active_player_receives_priority_CR117_4()
    {
        var execution = Ready().Pass(new(0)).CompleteCheckpoint(Identity);
        execution = execution.Act(new(1), s => AddSpell(AddSpell(s))).CompleteCheckpoint(Identity);
        var order = execution.State.In(ZoneKey.Shared(Zone.Stack));
        execution = PassRound(execution);
        Assert.Equal(ExecutionBoundary.Resolution, execution.Boundary);
        Assert.Equal(order[^1], execution.Resolving);
        Assert.Null(execution.State.Priority.Holder);
        Assert.Throws<InvalidOperationException>(() => execution.Pass(new(0)));
        execution = execution.Resolve((s, id) => ZoneTransitions.Move(s, new(id, ZoneKey.Of(Zone.Graveyard, new(0)))).State);
        Assert.Equal(ExecutionBoundary.Checkpoint, execution.Boundary);
        Assert.Single(execution.State.In(ZoneKey.Shared(Zone.Stack)));
        execution = execution.CompleteCheckpoint(Identity);
        Assert.Equal(new PlayerId(0), execution.State.Priority.Holder);
        execution = PassRound(execution);
        Assert.Equal(order[0], execution.Resolving);
        execution = execution.Resolve((s, id) => ZoneTransitions.Move(s, new(id, ZoneKey.Of(Zone.Graveyard, new(0)))).State)
            .CompleteCheckpoint(Identity);
        Assert.Equal(Step.Upkeep, execution.State.Turn.Step);
        Assert.Equal(Step.PrecombatMain, PassRound(execution).State.Turn.Step);
    }

    [Fact]
    public void Resolver_cannot_leave_top_or_resolve_the_whole_stack()
    {
        var execution = PassRound(Ready().Act(new(0), s => AddSpell(AddSpell(s))).CompleteCheckpoint(Identity));
        Assert.Throws<NotSupportedException>(() => execution.Resolve((s, _) => s));
        Assert.Throws<NotSupportedException>(() => execution.Resolve((s, _) =>
        {
            foreach (var id in s.In(ZoneKey.Shared(Zone.Stack)))
                s = ZoneTransitions.Move(s, new(id, ZoneKey.Of(Zone.Graveyard, new(0)))).State;
            return s;
        }));
        Assert.Equal(2, execution.State.In(ZoneKey.Shared(Zone.Stack)).Count);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Quiet_cleanup_advances_seat_and_next_turn_includes_draw_CR103_8(int seats)
    {
        var execution = Cleanup(seats);
        Assert.Null(execution.State.Priority.Holder);
        execution = execution.CompleteTurnAction(Identity).CompleteCheckpoint(Identity);
        Assert.Equal(2, execution.State.Turn.Number);
        Assert.Equal(new PlayerId(1), execution.State.Turn.ActivePlayer);
        Assert.Equal(Step.Untap, execution.State.Turn.Step);
        execution = execution.CompleteTurnAction(Identity).CompleteCheckpoint(Identity);
        Assert.Equal(Step.Draw, PassRound(execution).State.Turn.Step);
    }

    [Fact]
    public void Cleanup_activity_grants_priority_and_all_passes_repeat_cleanup_CR514_3a()
    {
        var execution = Cleanup().CompleteTurnAction(Identity).CompleteCheckpoint(Identity, cleanupActivity: true);
        Assert.Equal(ExecutionBoundary.Priority, execution.Boundary);
        execution = PassRound(execution);
        Assert.Equal(Step.Cleanup, execution.State.Turn.Step);
        Assert.Equal(1, execution.State.Turn.Number);
        Assert.Equal(ExecutionBoundary.TurnAction, execution.Boundary);
        execution = execution.CompleteTurnAction(Identity).CompleteCheckpoint(Identity);
        Assert.Equal(2, execution.State.Turn.Number);
    }

    [Fact]
    public void Empty_combat_retains_declare_attackers_and_end_combat_priority_CR508_8()
    {
        var execution = Ready();
        execution = PassRound(execution).CompleteCheckpoint(Identity); // main
        execution = PassRound(execution).CompleteCheckpoint(Identity); // beginning of combat
        execution = PassRound(execution);
        Assert.Equal(Step.DeclareAttackers, execution.State.Turn.Step);
        Assert.Throws<NotSupportedException>(() => execution.CompleteTurnAction(Identity));
        execution = execution.CompleteTurnAction(s => s with
        {
            Combat = new(s.Turn.ActivePlayer, ImmutableDictionary<ObjectId, CombatTarget>.Empty,
                ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty, true, false),
        }).CompleteCheckpoint(Identity);
        Assert.Equal(Step.DeclareAttackers, execution.State.Turn.Step);
        execution = PassRound(execution).CompleteCheckpoint(Identity);
        Assert.Equal(Step.EndOfCombat, execution.State.Turn.Step);
        execution = PassRound(execution);
        Assert.Equal(Step.PostcombatMain, execution.State.Turn.Step);
        Assert.Null(execution.State.Combat);
    }

    [Fact]
    public void Mana_empties_at_step_end_not_each_priority_pass_CR500_5()
    {
        var execution = Ready().Act(new(0), s => s with
        {
            Players = s.Players.SetItem(0, s.Players[0] with { Mana = new(1, 0, 0, 0, 0, 0) }),
        }).CompleteCheckpoint(Identity);
        execution = execution.Pass(new(0)).CompleteCheckpoint(Identity);
        Assert.Equal(1, execution.State.Players[0].Mana.Total);
        execution = execution.Pass(new(1));
        Assert.All(execution.State.Players, p => Assert.Equal(ManaPool.Empty, p.Mana));
    }

    [Fact]
    public void Providers_cannot_smuggle_priority_turn_changes_or_unimplemented_choices()
    {
        var execution = Ready();
        Assert.Throws<NotSupportedException>(() => execution.Act(new(0), s => s with { Priority = new(new(1), []) }));
        Assert.Throws<NotSupportedException>(() => execution.Act(new(0), s => s with { Turn = s.Turn with { Step = Step.End } }));
        Assert.Throws<NotSupportedException>(() => execution.Act(new(0), s => s with
        {
            Pending = new(new(1), new(1), "choice", "Choose", [], 0, 0),
        }));
        Assert.Equal(new PlayerId(0), execution.State.Priority.Holder);
    }

    [Fact]
    public void Finished_checkpoint_stops_execution()
    {
        var execution = Ready().Act(new(0), Identity).CompleteCheckpoint(s => s with { Outcome = new([new(0)], false, "Fixture") });
        Assert.Equal(ExecutionBoundary.Finished, execution.Boundary);
        Assert.Null(execution.State.Priority.Holder);
        Assert.Throws<InvalidOperationException>(() => execution.Pass(new(0)));
    }

    [Fact]
    public void Extra_turns_and_departed_players_are_explicitly_unsupported()
    {
        var state = Setup();
        Assert.Throws<NotSupportedException>(() => TurnExecution.Start(state with { Turn = state.Turn with { ExtraTurns = [new(1)] } }, new(0)));
        Assert.Throws<NotSupportedException>(() => TurnExecution.Start(state with { Players = state.Players.SetItem(1, state.Players[1] with { HasLost = true }) }, new(0)));
    }

    [Fact]
    public void Basic_untap_uses_control_and_cleanup_clears_even_phased_out_damage()
    {
        var definition = new CardDefinition("permanent", "Permanent", Layout.Normal,
            [new Face("Permanent", FaceRole.Main, new TypeLine(CardType.Creature))]);
        var state = GameSetup.AddCard(Setup(), new(1), definition, "permanent");
        var moved = ZoneTransitions.Move(state, new(new(1), ZoneKey.Shared(Zone.Battlefield), Controller: new(0)));
        state = moved.State with
        {
            Objects = moved.State.Objects.SetItem(moved.Current,
            moved.State.Objects[moved.Current] with { Tapped = true, Damage = 3, DealtDeathtouchDamage = true })
        };
        var execution = TurnExecution.Start(state, new(0)).CompleteBasicTurnAction();
        Assert.False(execution.State.Objects[moved.Current].Tapped);
        Assert.False(execution.State.Objects[moved.Current].SummoningSick);
        Assert.True(state.Objects[moved.Current].Tapped);
        execution = Cleanup().CompleteTurnAction(s => s with
        {
            Objects = state.Objects.SetItem(moved.Current, state.Objects[moved.Current] with { PhasedOut = true }),
            Zones = state.Zones,
            LastObjectId = state.LastObjectId,
            LastTimestamp = state.LastTimestamp,
        }).CompleteCheckpoint(Identity, cleanupActivity: true);
        execution = PassRound(execution).CompleteBasicTurnAction();
        Assert.Equal(0, execution.State.Objects[moved.Current].Damage);
        Assert.False(execution.State.Objects[moved.Current].DealtDeathtouchDamage);
        Assert.True(execution.State.Objects[moved.Current].PhasedOut);
    }

    [Fact]
    public void Multiplayer_basic_draw_moves_top_card_before_priority_CR504_1()
    {
        var definition = new CardDefinition("card", "Card", Layout.Normal,
            [new Face("Card", FaceRole.Main, new TypeLine(CardType.Creature))]);
        var state = GameSetup.AddCard(Setup(3), new(0), definition, "card");
        var execution = TurnExecution.Start(state, new(0)).CompleteBasicTurnAction().CompleteCheckpoint(Identity);
        execution = PassRound(execution);
        Assert.Equal(Step.Draw, execution.State.Turn.Step);
        Assert.Empty(execution.State.In(ZoneKey.Of(Zone.Hand, new(0))));
        execution = execution.CompleteBasicTurnAction();
        Assert.Single(execution.State.In(ZoneKey.Of(Zone.Hand, new(0))));
        Assert.Equal(1, execution.State.Players[0].ObjectsDrawnThisTurn);
        Assert.Null(execution.State.Priority.Holder);
        Assert.Equal(ExecutionBoundary.Checkpoint, execution.Boundary);
    }

    [Fact]
    public void Failed_basic_draw_records_marker_and_waits_for_SBA_provider()
    {
        var execution = PassRound(Ready(3)).CompleteBasicTurnAction();
        Assert.True(execution.State.Players[0].DrewFromEmptyLibrary);
        Assert.False(execution.State.Players[0].HasLost);
        Assert.Equal(0, execution.State.Players[0].ObjectsDrawnThisTurn);
        Assert.Equal(ExecutionBoundary.Checkpoint, execution.Boundary);
    }

    [Fact]
    public void Checkpoint_stack_insertion_interrupts_prior_pass_sequence()
    {
        var execution = Ready(3).Pass(new(0)).CompleteCheckpoint(AddSpell);
        Assert.Empty(execution.State.Priority.PassedInSuccession);
        execution = execution.Pass(new(1)).CompleteCheckpoint(Identity).Pass(new(2)).CompleteCheckpoint(Identity);
        Assert.Equal(ExecutionBoundary.Priority, execution.Boundary);
        Assert.Equal(ExecutionBoundary.Resolution, execution.Pass(new(0)).Boundary);
    }

    [Fact]
    public void Cleanup_stack_resolution_returns_priority_then_repeats_cleanup()
    {
        var execution = Cleanup().CompleteTurnAction(Identity).CompleteCheckpoint(AddSpell, cleanupActivity: true);
        execution = PassRound(execution).Resolve((s, id) =>
            ZoneTransitions.Move(s, new(id, ZoneKey.Of(Zone.Graveyard, new(0)))).State).CompleteCheckpoint(Identity);
        Assert.Equal(Step.Cleanup, execution.State.Turn.Step);
        Assert.Equal(ExecutionBoundary.Priority, execution.Boundary);
        execution = PassRound(execution);
        Assert.Equal(Step.Cleanup, execution.State.Turn.Step);
        Assert.Equal(ExecutionBoundary.TurnAction, execution.Boundary);
    }

    [Fact]
    public void Basic_cleanup_rejects_required_discard_without_removing_damage()
    {
        var execution = Cleanup().CompleteTurnAction(s => s with
        {
            Players = s.Players.SetItem(0, s.Players[0] with { MaxHandSize = -1 }),
        }).CompleteCheckpoint(Identity, cleanupActivity: true);
        execution = PassRound(execution);
        Assert.Throws<NotSupportedException>(() => execution.CompleteBasicTurnAction());
        Assert.Equal(ExecutionBoundary.TurnAction, execution.Boundary);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Turn_order_wraps_from_last_seat_and_phases_match_steps(int seats)
    {
        var execution = Ready(seats, seats - 1);
        Assert.Equal(Phase.Beginning, execution.Phase);
        while (execution.State.Turn.Step != Step.Cleanup)
        {
            execution = PassRound(execution);
            if (execution.Boundary == ExecutionBoundary.TurnAction)
            {
                if (execution.State.Turn.Step == Step.Cleanup)
                    break;
                execution = execution.CompleteTurnAction(s => s.Turn.Step == Step.DeclareAttackers
                    ? s with
                    {
                        Combat = new(s.Turn.ActivePlayer, ImmutableDictionary<ObjectId, CombatTarget>.Empty,
                            ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty, true, false),
                    } : s);
            }
            execution = execution.CompleteCheckpoint(Identity);
            if (execution.State.Turn.Step == Step.PrecombatMain)
                Assert.Equal(Phase.PrecombatMain, execution.Phase);
            if (execution.State.Turn.Step == Step.EndOfCombat)
                Assert.Equal(Phase.Combat, execution.Phase);
            if (execution.State.Turn.Step == Step.PostcombatMain)
                Assert.Equal(Phase.PostcombatMain, execution.Phase);
        }
        Assert.Equal(Phase.Ending, execution.Phase);
        execution = execution.CompleteBasicTurnAction().CompleteCheckpoint(Identity);
        Assert.Equal(new PlayerId(0), execution.State.Turn.ActivePlayer);
        Assert.Equal(0, execution.State.Turn.TurnOrderIndex);
    }
}
