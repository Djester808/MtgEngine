using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

public sealed class BasicCombatTests
{
    private static readonly PlayerId A = new(0);
    private static readonly PlayerId B = new(1);
    private static readonly PlayerId C = new(2);
    private static readonly CardDefinition Bear = Creature("bear", 2, 2);
    private static readonly CardDefinition Giant = Creature("giant", 3, 3);
    private static readonly CardDefinition Small = Creature("small", 1, 1);
    private static readonly CardDefinition Zero = Creature("zero", 0, 0);
    private static readonly CardDefinition Negative = Creature("negative", -2, 3);
    private static readonly CardDefinition Forest = new("forest", "Forest", Layout.Normal,
        [new("Forest", FaceRole.Main, new(CardType.Land, Supertype.Basic, ["Forest"]))]);
    private static readonly CardDefinition Bounce = new("bounce", "Bounce", Layout.Normal,
        [new("Bounce", FaceRole.Main, new(CardType.Instant), "{0}", OracleText: "Return target creature to its owner's hand.")]);
    private static readonly BasicActions Actions = new([new(Bear, BasicBehavior.Creature), new(Giant, BasicBehavior.Creature),
        new(Small, BasicBehavior.Creature), new(Zero, BasicBehavior.Creature), new(Negative, BasicBehavior.Creature),
        new(Forest, BasicBehavior.Land), new(Bounce, BasicBehavior.ReturnCreature)]);
    private static readonly BasicCombat Combat = new(Actions);

    private static CardDefinition Creature(string name, int power, int toughness) => new(name, name, Layout.Normal,
        [new(name, FaceRole.Main, new(CardType.Creature), "{G}", new(power.ToString()), new(toughness.ToString()))]);

    private static GameState Setup(int seats = 2, int life = 20)
    {
        var state = GameSetup.Create(Enumerable.Range(0, seats).Select(i => PlayerState.Create(new(i), "Seat", life)));
        foreach (var player in state.Players)
            for (var i = 0; i < 6; i++)
                state = GameSetup.AddCard(state, player.Id, Forest, new("forest"));
        return state;
    }

    private static (GameState State, ObjectId Id) Add(GameState state, CardDefinition definition, PlayerId owner, Zone zone = Zone.Battlefield)
    {
        state = GameSetup.AddCard(state, owner, definition, new(definition.OracleId));
        var move = ZoneTransitions.Move(state, new(new(state.LastObjectId), zone == Zone.Battlefield
            ? ZoneKey.Shared(zone) : ZoneKey.Of(zone, owner)));
        return (move.State, move.Current);
    }

    private static TurnExecution Check(TurnExecution execution) => Combat.CompleteCheckpoint(execution);
    private static TurnExecution Start(GameState state) => Check(TurnExecution.Start(state, A).CompleteBasicTurnAction());

    private static TurnExecution Round(TurnExecution execution)
    {
        var count = execution.State.Players.Count(p => !p.HasLost) - execution.State.Priority.PassedInSuccession.Count;
        for (var i = 0; i < count; i++)
        {
            execution = execution.Pass(execution.State.Priority.Holder!.Value);
            if (i < count - 1)
                execution = Check(execution);
        }
        return execution;
    }

    private static TurnExecution Reach(TurnExecution execution, Step step, int turn = 1)
    {
        for (var i = 0; i < 200; i++)
        {
            if (execution.State.Turn.Step == step && execution.State.Turn.Number == turn)
                return execution;
            execution = execution.Boundary switch
            {
                ExecutionBoundary.Checkpoint => Check(execution),
                ExecutionBoundary.Priority => Round(execution),
                ExecutionBoundary.TurnAction when execution.State.Turn.Step == Step.DeclareAttackers =>
                    Combat.DeclareAttackers(execution, execution.State.Player(execution.State.Turn.ActivePlayer).HasLost
                        ? TurnExecution.Next(execution.State, execution.State.Turn.ActivePlayer) : execution.State.Turn.ActivePlayer, []),
                ExecutionBoundary.TurnAction => execution.CompleteBasicTurnAction(),
                _ => throw new InvalidOperationException("Unexpected boundary in test driver."),
            };
        }
        throw new InvalidOperationException("Test driver did not reach requested step.");
    }

    private static TurnExecution Attacks(GameState state) => Reach(Start(state), Step.DeclareAttackers);
    private static TurnExecution Blocks(TurnExecution attacks) => Round(Check(attacks));
    private static TurnExecution Damage(TurnExecution blocks) => Round(Check(blocks));

    [Fact]
    public void Attacks_are_atomic_tap_creatures_and_require_current_control_and_no_sickness()
    {
        var (state, bear) = Add(Setup(), Bear, A);
        (state, var enemy) = Add(state, Bear, B);
        var execution = Attacks(state);
        Assert.Throws<InvalidOperationException>(() => Combat.DeclareAttackers(execution, B, []));
        Assert.Throws<InvalidOperationException>(() => Combat.DeclareAttackers(execution, A, [new(bear, B), new(enemy, B)]));
        Assert.Throws<InvalidOperationException>(() => Combat.DeclareAttackers(execution, A, [new(bear, A)]));
        Assert.Throws<InvalidOperationException>(() => Combat.DeclareAttackers(execution, A, [new(bear, B), new(bear, B)]));
        Assert.False(execution.State.Objects[bear].Tapped);
        var declared = Combat.DeclareAttackers(execution, A, [new(bear, B)]);
        Assert.True(declared.State.Objects[bear].Tapped);
        Assert.Null(declared.State.Priority.Holder);
        Assert.Throws<InvalidOperationException>(() => Combat.DeclareAttackers(declared, A, []));
    }

    [Fact]
    public void Newly_cast_creature_cannot_attack_until_its_controllers_next_turn_then_game_finishes()
    {
        var (state, bear) = Add(Setup(life: 2), Bear, A, Zone.Hand);
        (state, var forest) = Add(state, Forest, A, Zone.Hand);
        var execution = Check(Reach(Start(state), Step.PrecombatMain));
        execution = Check(Actions.PlayLand(execution, A, forest));
        forest = Assert.Single(execution.State.In(ZoneKey.Shared(Zone.Battlefield)));
        execution = Check(Actions.Cast(execution, A, new(bear, new(0, 0, 0, 0, 1, 0), ManaSources: [forest])));
        execution = Check(Actions.Resolve(Round(execution)));
        bear = Assert.Single(execution.State.Objects.Values, o => o.IsPermanent && o.Definition.OracleId == "bear").Id;
        execution = Reach(execution, Step.DeclareAttackers);
        Assert.True(execution.State.Objects[bear].SummoningSick);
        Assert.Throws<InvalidOperationException>(() => Combat.DeclareAttackers(execution, A, [new(bear, B)]));
        execution = Reach(Combat.DeclareAttackers(execution, A, []), Step.DeclareAttackers, turn: 3);
        Assert.False(execution.State.Objects[bear].SummoningSick);
        execution = Blocks(Combat.DeclareAttackers(execution, A, [new(bear, B)]));
        execution = Damage(Combat.DeclareBlockers(execution, B, []));
        var dealt = Combat.DealDamage(execution, A);
        Assert.False(dealt.State.Player(B).HasLost); // Damage first; losses at the checkpoint.
        execution = Check(dealt);
        Assert.Equal(ExecutionBoundary.Finished, execution.Boundary);
        Assert.Equal([A], execution.State.Outcome!.Winners);
        Assert.False(execution.State.Outcome.Draw);
        Assert.Null(execution.State.Priority.Holder);
        Assert.Throws<InvalidOperationException>(() => execution.Pass(A));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Multiple_defenders_declare_in_APNAP_order_and_cannot_cross_block(int seats)
    {
        var (state, first) = Add(Setup(seats), Bear, A);
        (state, var second) = Add(state, Bear, A);
        (state, var blockB) = Add(state, Bear, B);
        (state, var blockC) = Add(state, Small, C);
        var execution = Blocks(Combat.DeclareAttackers(Attacks(state), A, [new(first, B), new(second, C)]));
        Assert.Equal(B, Combat.NextDefender(execution));
        Assert.Throws<InvalidOperationException>(() => Combat.DeclareBlockers(execution, C, [new(blockC, second)]));
        Assert.Throws<InvalidOperationException>(() => Combat.DeclareBlockers(execution, B, [new(blockB, second)]));
        execution = Combat.DeclareBlockers(execution, B, [new(blockB, first)]);
        Assert.Equal(ExecutionBoundary.TurnAction, execution.Boundary);
        Assert.Null(execution.State.Priority.Holder);
        Assert.Equal(C, Combat.NextDefender(execution));
        Assert.Throws<InvalidOperationException>(() => Combat.DeclareBlockers(execution, B, []));
        execution = Damage(Combat.DeclareBlockers(execution, C, []));
        execution = Combat.DealDamage(execution, A);
        Assert.Equal(2, execution.State.Objects[first].Damage);
        Assert.Equal(2, execution.State.Objects[blockB].Damage);
        Assert.Equal(18, execution.State.Player(C).Life);
        Assert.Equal(20, execution.State.Player(B).Life);
        execution = Check(execution);
        Assert.False(execution.State.Objects.ContainsKey(first));
        Assert.False(execution.State.Objects.ContainsKey(blockB));
        Assert.True(execution.State.Objects.ContainsKey(second));
        Assert.True(execution.State.Objects.ContainsKey(blockC));
    }

    [Fact]
    public void Summoning_sick_blocker_can_block_and_does_not_tap()
    {
        var (state, attacker) = Add(Setup(), Bear, A);
        (state, var blocker) = Add(state, Bear, B);
        var execution = Blocks(Combat.DeclareAttackers(Attacks(state), A, [new(attacker, B)]));
        Assert.True(execution.State.Objects[blocker].SummoningSick);
        execution = Combat.DeclareBlockers(execution, B, [new(blocker, attacker)]);
        Assert.False(execution.State.Objects[blocker].Tapped);
        Assert.Equal([blocker], execution.State.Combat!.Blockers[attacker]);
    }

    [Fact]
    public void Damage_split_is_explicit_atomic_and_does_not_require_lethal_before_another_blocker()
    {
        var (state, attacker) = Add(Setup(), Giant, A);
        (state, var first) = Add(state, Bear, B);
        (state, var second) = Add(state, Bear, B);
        var execution = Damage(Combat.DeclareBlockers(Blocks(Combat.DeclareAttackers(Attacks(state), A,
            [new(attacker, B)])), B, [new(first, attacker), new(second, attacker)]));
        Assert.Throws<InvalidOperationException>(() => Combat.DealDamage(execution, B));
        Assert.Throws<InvalidOperationException>(() => Combat.DealDamage(execution, A));
        Assert.Throws<InvalidOperationException>(() => Combat.DealDamage(execution, A,
            [new(attacker, ImmutableDictionary<ObjectId, int>.Empty.Add(first, 4))]));
        Assert.Equal(0, execution.State.Objects[first].Damage);
        execution = Combat.DealDamage(execution, A, [new(attacker,
            ImmutableDictionary<ObjectId, int>.Empty.Add(first, 1).Add(second, 2))]);
        Assert.Equal(4, execution.State.Objects[attacker].Damage);
        Assert.Equal(1, execution.State.Objects[first].Damage);
        Assert.Equal(2, execution.State.Objects[second].Damage);
        Assert.Throws<InvalidOperationException>(() => Combat.DealDamage(execution, A));
        execution = Check(execution);
        Assert.True(execution.State.Objects.ContainsKey(first));
        Assert.False(execution.State.Objects.ContainsKey(second));
        Assert.False(execution.State.Objects.ContainsKey(attacker));
    }

    [Fact]
    public void Bouncing_all_blockers_does_not_make_attacker_unblocked_CR509_1h()
    {
        var (state, attacker) = Add(Setup(), Bear, A);
        (state, var blocker) = Add(state, Bear, B);
        (state, var bounce) = Add(state, Bounce, A, Zone.Hand);
        var execution = Check(Combat.DeclareBlockers(Blocks(Combat.DeclareAttackers(Attacks(state), A,
            [new(attacker, B)])), B, [new(blocker, attacker)]));
        execution = Check(Actions.Cast(execution, A, new(bounce, ManaPool.Empty, blocker)));
        execution = Check(Actions.Resolve(Round(execution)));
        var returned = Assert.Single(execution.State.In(ZoneKey.Of(Zone.Hand, B)));
        Assert.NotEqual(blocker, returned);
        execution = Check(Combat.DealDamage(Round(execution), A));
        Assert.Equal(20, execution.State.Player(B).Life);
        Assert.Equal(0, execution.State.Objects[attacker].Damage);
    }

    [Fact]
    public void Attacker_removed_by_response_deals_no_damage_and_empty_blocks_can_complete()
    {
        var (state, attacker) = Add(Setup(), Bear, A);
        (state, var bounce) = Add(state, Bounce, A, Zone.Hand);
        var execution = Check(Combat.DeclareAttackers(Attacks(state), A, [new(attacker, B)]));
        execution = Check(Actions.Cast(execution, A, new(bounce, ManaPool.Empty, attacker)));
        execution = Check(Actions.Resolve(Round(execution)));
        execution = Round(execution);
        Assert.Null(Combat.NextDefender(execution));
        execution = Damage(Combat.DeclareBlockers(execution, A, []));
        execution = Check(Combat.DealDamage(execution, A));
        Assert.Equal(20, execution.State.Player(B).Life);
    }

    [Fact]
    public void Zero_toughness_and_lethal_damage_die_in_one_checkpoint_with_new_identity()
    {
        var (state, zero) = Add(Setup(), Zero, A);
        (state, var bear) = Add(state, Bear, B);
        state = state with { Objects = state.Objects.SetItem(bear, state.Objects[bear] with { Damage = 2 }) };
        var execution = Start(state);
        Assert.False(execution.State.Objects.ContainsKey(zero));
        Assert.False(execution.State.Objects.ContainsKey(bear));
        var deadBear = execution.State.Objects[Assert.Single(execution.State.In(ZoneKey.Of(Zone.Graveyard, B)))];
        Assert.Equal(state.Objects[bear].CardInstanceId, deadBear.CardInstanceId);
        Assert.Equal(0, deadBear.Damage);
        Assert.Equal(A, execution.State.Priority.Holder);
    }

    [Fact]
    public void Negative_power_deals_no_damage_but_still_receives_blocker_damage()
    {
        var (state, attacker) = Add(Setup(), Negative, A);
        (state, var blocker) = Add(state, Small, B);
        var execution = Damage(Combat.DeclareBlockers(Blocks(Combat.DeclareAttackers(Attacks(state), A,
            [new(attacker, B)])), B, [new(blocker, attacker)]));
        execution = Check(Combat.DealDamage(execution, A));
        Assert.Equal(1, execution.State.Objects[attacker].Damage);
        Assert.Equal(0, execution.State.Objects[blocker].Damage);
    }

    [Fact]
    public void Eliminated_defender_leaves_with_all_owned_cards_and_remaining_seats_keep_playing()
    {
        var (state, attacker) = Add(Setup(3, life: 2), Bear, A);
        (state, _) = Add(state, Forest, B, Zone.Hand);
        var execution = Damage(Combat.DeclareBlockers(Blocks(Combat.DeclareAttackers(Attacks(state), A,
            [new(attacker, B)])), B, []));
        execution = Check(Combat.DealDamage(execution, A));
        Assert.True(execution.State.Player(B).HasLost);
        Assert.Null(execution.State.Outcome);
        Assert.DoesNotContain(execution.State.Objects.Values, o => o.Owner == B);
        Assert.All(execution.State.Zones.Where(z => z.Key.Player == B), z => Assert.Empty(z.Value));
        execution.State.Validate();
        execution = Check(execution.Pass(A));
        Assert.Equal(C, execution.State.Priority.Holder);
        execution = Reach(execution, Step.Untap, turn: 2);
        Assert.Equal(C, execution.State.Turn.ActivePlayer);
    }

    [Fact]
    public void Simultaneous_losses_produce_draw_or_last_survivor_without_transient_winner()
    {
        var state = Setup();
        state = state with { Players = state.Players.Select(p => p with { Life = 0 }).ToImmutableList() };
        var draw = Start(state);
        Assert.True(draw.State.Outcome!.Draw);
        Assert.Empty(draw.State.Outcome.Winners);
        Assert.All(draw.State.Players, p => Assert.True(p.HasLost));
        state = Setup(3);
        state = state with { Players = state.Players.Select(p => p.Id == C ? p : p with { DrewFromEmptyLibrary = true }).ToImmutableList() };
        var win = Start(state);
        Assert.Equal([C], win.State.Outcome!.Winners);
        Assert.False(win.State.Outcome.Draw);
    }

    [Fact]
    public void Active_player_failed_draw_loses_but_turn_continues_with_next_living_priority()
    {
        var state = GameSetup.Create(Enumerable.Range(0, 3).Select(i => PlayerState.Create(new(i), "Seat", 20)));
        var execution = Round(Start(state));
        Assert.Equal(Step.Draw, execution.State.Turn.Step);
        execution = Check(execution.CompleteBasicTurnAction());
        Assert.True(execution.State.Player(A).HasLost);
        Assert.Equal(A, execution.State.Turn.ActivePlayer);
        Assert.Equal(B, execution.State.Priority.Holder);
        execution = Reach(execution, Step.Untap, turn: 2);
        Assert.Equal(B, execution.State.Turn.ActivePlayer);
    }

    [Fact]
    public void Departure_with_cross_player_control_is_explicitly_rejected_atomically()
    {
        var (state, bear) = Add(Setup(3), Bear, A);
        state = ZoneTransitions.ChangeController(state, bear, B);
        state = state with { Players = state.Players.SetItem(1, state.Player(B) with { Life = 0 }) };
        var execution = TurnExecution.Start(state, A).CompleteBasicTurnAction();
        Assert.Throws<NotSupportedException>(() => Check(execution));
        Assert.False(execution.State.Player(B).HasLost);
        Assert.True(execution.State.Objects.ContainsKey(bear));
    }

    [Fact]
    public void Control_change_away_and_back_removes_attacker_from_combat()
    {
        var (state, attacker) = Add(Setup(), Bear, A);
        var execution = Check(Combat.DeclareAttackers(Attacks(state), A, [new(attacker, B)]));
        execution = Check(execution.Act(A, s => ZoneTransitions.ChangeController(ZoneTransitions.ChangeController(s, attacker, B), attacker, A)));
        execution = Round(execution);
        execution = Damage(Combat.DeclareBlockers(execution, A, []));
        execution = Check(Combat.DealDamage(execution, A));
        Assert.Equal(20, execution.State.Player(B).Life);
        Assert.True(execution.State.Objects[attacker].SummoningSick);
    }

    [Fact]
    public void Two_opponents_eliminated_by_same_damage_batch_give_one_final_winner()
    {
        var (state, first) = Add(Setup(3, life: 2), Bear, A);
        (state, var second) = Add(state, Bear, A);
        var execution = Blocks(Combat.DeclareAttackers(Attacks(state), A, [new(first, B), new(second, C)]));
        execution = Combat.DeclareBlockers(execution, B, []);
        execution = Damage(Combat.DeclareBlockers(execution, C, []));
        execution = Combat.DealDamage(execution, A);
        Assert.Equal(0, execution.State.Player(B).Life);
        Assert.Equal(0, execution.State.Player(C).Life);
        Assert.Null(execution.State.Outcome);
        execution = Check(execution);
        Assert.Equal([A], execution.State.Outcome!.Winners);
        Assert.True(execution.State.Player(B).HasLost);
        Assert.True(execution.State.Player(C).HasLost);
    }

    [Fact]
    public void Tapped_attacker_and_blocker_are_rejected_and_duplicate_blocker_cannot_block_twice()
    {
        var (state, attacker) = Add(Setup(), Bear, A);
        (state, var blocker) = Add(state, Bear, B);
        var execution = Start(state);
        execution = Check(execution.Act(A, s => s with
        {
            Objects = s.Objects.SetItem(attacker, s.Objects[attacker] with { Tapped = true }),
        }));
        execution = Reach(execution, Step.DeclareAttackers);
        Assert.Throws<InvalidOperationException>(() => Combat.DeclareAttackers(execution, A, [new(attacker, B)]));

        state = state with { Objects = state.Objects.SetItem(blocker, state.Objects[blocker] with { Tapped = true }) };
        execution = Blocks(Combat.DeclareAttackers(Attacks(state), A, [new(attacker, B)]));
        Assert.Throws<InvalidOperationException>(() => Combat.DeclareBlockers(execution, B, [new(blocker, attacker)]));
        state = state with { Objects = state.Objects.SetItem(blocker, state.Objects[blocker] with { Tapped = false }) };
        execution = Blocks(Combat.DeclareAttackers(Attacks(state), A, [new(attacker, B)]));
        Assert.Throws<InvalidOperationException>(() => Combat.DeclareBlockers(execution, B,
            [new(blocker, attacker), new(blocker, attacker)]));
    }

    [Fact]
    public void Surviving_damage_persists_through_combat_and_clears_in_cleanup()
    {
        var (state, attacker) = Add(Setup(), Giant, A);
        (state, var blocker) = Add(state, Small, B);
        var execution = Damage(Combat.DeclareBlockers(Blocks(Combat.DeclareAttackers(Attacks(state), A,
            [new(attacker, B)])), B, [new(blocker, attacker)]));
        execution = Check(Combat.DealDamage(execution, A));
        execution = Reach(execution, Step.PostcombatMain);
        Assert.Null(execution.State.Combat);
        Assert.Equal(1, execution.State.Objects[attacker].Damage);
        execution = Reach(execution, Step.Cleanup).CompleteBasicTurnAction();
        Assert.Equal(0, execution.State.Objects[attacker].Damage);
    }

    [Fact]
    public void Departed_priority_recipient_is_skipped_and_their_stack_cards_leave()
    {
        var (state, bounce) = Add(Setup(3), Bounce, B, Zone.Hand);
        (state, var bear) = Add(state, Bear, A);
        var execution = Check(Start(state).Pass(A));
        execution = Check(Actions.Cast(execution, B, new(bounce, ManaPool.Empty, bear)));
        execution = execution.Act(B, s => s with { Players = s.Players.SetItem(1, s.Player(B) with { Life = 0 }) });
        execution = Check(execution);
        Assert.Equal(C, execution.State.Priority.Holder);
        Assert.Empty(execution.State.Priority.PassedInSuccession);
        Assert.Empty(execution.State.In(ZoneKey.Shared(Zone.Stack)));
        Assert.Throws<InvalidOperationException>(() => execution.Pass(B));
        Assert.Equal(Step.Draw, Round(execution).State.Turn.Step);
    }

    [Fact]
    public void Cleanup_SBA_activity_grants_priority_then_repeats_cleanup()
    {
        var (state, bear) = Add(Setup(), Bear, A);
        var execution = Reach(Start(state), Step.Cleanup).CompleteTurnAction(s => s with
        {
            Objects = s.Objects.SetItem(bear, s.Objects[bear] with { Damage = 2 }),
        });
        execution = Check(execution);
        Assert.Equal(ExecutionBoundary.Priority, execution.Boundary);
        Assert.Equal(Step.Cleanup, execution.State.Turn.Step);
        execution = Round(execution);
        Assert.Equal(ExecutionBoundary.TurnAction, execution.Boundary);
        Assert.Equal(Step.Cleanup, execution.State.Turn.Step);
        execution = Check(execution.CompleteBasicTurnAction());
        Assert.Equal(2, execution.State.Turn.Number);
    }

    [Fact]
    public void Commander_designation_is_not_silently_treated_as_ordinary_combat()
    {
        var (state, bear) = Add(Setup(), Bear, A);
        state = state with { Objects = state.Objects.SetItem(bear, state.Objects[bear] with { IsCommander = true }) };
        Assert.Throws<NotSupportedException>(() => Start(state));
    }
}
