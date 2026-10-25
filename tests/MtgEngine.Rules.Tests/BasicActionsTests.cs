using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

public sealed class BasicActionsTests
{
    private static readonly PlayerId A = new(0);
    private static readonly PlayerId B = new(1);
    private static readonly CardDefinition Island = Card("island", CardType.Land, null, subtype: "Island");
    private static readonly CardDefinition Forest = Card("forest", CardType.Land, null, subtype: "Forest");
    private static readonly CardDefinition Bear = Card("bear", CardType.Creature, "{1}{G}");
    private static readonly CardDefinition Bounce = Card("bounce", CardType.Instant, "{U}", "Return target creature to its owner's hand.");
    private static readonly CardDefinition Life = Card("life", CardType.Instant, "{1}", "Target player gains 3 life.");
    private static readonly BasicActions Actions = new([new(Island, BasicBehavior.Land), new(Forest, BasicBehavior.Land),
        new(Bear, BasicBehavior.Creature), new(Bounce, BasicBehavior.ReturnCreature), new(Life, BasicBehavior.GainLife, 3)]);

    private static CardDefinition Card(string name, CardType type, string? cost, string? text = null, string? subtype = null) =>
        new(name, name, Layout.Normal, [new(name, FaceRole.Main,
            new(type, subtype is null ? Supertype.None : Supertype.Basic, subtype is null ? [] : [subtype]),
            cost, type.HasFlag(CardType.Creature) ? new("2") : null,
            type.HasFlag(CardType.Creature) ? new("2") : null, OracleText: text)]);

    private static GameState Setup(int seats = 2) => GameSetup.Create(
        Enumerable.Range(0, seats).Select(i => PlayerState.Create(new(i), "Seat", 20)));

    private static (GameState State, ObjectId Id) Add(GameState state, CardDefinition definition, PlayerId owner, Zone zone)
    {
        state = GameSetup.AddCard(state, owner, definition, new(definition.OracleId));
        var move = ZoneTransitions.Move(state, new(new(state.LastObjectId), zone == Zone.Battlefield
            ? ZoneKey.Shared(zone) : ZoneKey.Of(zone, owner)));
        return (move.State, move.Current);
    }

    private static TurnExecution Ready(GameState state)
    {
        var execution = Actions.CompleteCheckpoint(TurnExecution.Start(state, A).CompleteBasicTurnAction());
        execution = Round(execution);
        if (execution.Boundary == ExecutionBoundary.TurnAction)
            execution = execution.CompleteTurnAction(s => s); // Multiplayer fixture supplies its draw separately.
        execution = Actions.CompleteCheckpoint(execution);
        if (execution.State.Turn.Step == Step.Draw)
            execution = Actions.CompleteCheckpoint(Round(execution));
        return execution;
    }

    private static TurnExecution Round(TurnExecution execution)
    {
        var seats = execution.State.Players.Count;
        for (var i = 0; i < seats; i++)
        {
            execution = execution.Pass(execution.State.Priority.Holder!.Value);
            if (i + 1 < seats)
                execution = Actions.CompleteCheckpoint(execution);
        }
        return execution;
    }

    [Fact]
    public void Land_is_a_once_per_turn_special_action_with_new_object_identity()
    {
        var (state, land) = Add(Setup(), Forest, A, Zone.Hand);
        var (more, second) = Add(state, Island, A, Zone.Hand);
        var execution = Ready(more);
        var physical = execution.State.Objects[land].CardInstanceId;
        execution = Actions.CompleteCheckpoint(Actions.PlayLand(execution, A, land));
        var permanent = Assert.Single(execution.State.In(ZoneKey.Shared(Zone.Battlefield)));
        Assert.NotEqual(land, permanent);
        Assert.Equal(physical, execution.State.Objects[permanent].CardInstanceId);
        Assert.Empty(execution.State.In(ZoneKey.Shared(Zone.Stack)));
        Assert.Equal(A, execution.State.Priority.Holder);
        Assert.Equal(1, execution.State.Player(A).LandsPlayedThisTurn);
        Assert.Throws<InvalidOperationException>(() => Actions.PlayLand(execution, A, second));
        Assert.Throws<InvalidOperationException>(() => Actions.PlayLand(execution, A, land));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Creature_cast_pays_exact_mana_then_resolves_after_all_seats_pass(int seats)
    {
        var (state, forest) = Add(Setup(seats), Forest, A, Zone.Hand);
        (state, var island) = Add(state, Island, A, Zone.Battlefield);
        (state, var bear) = Add(state, Bear, A, Zone.Hand);
        var execution = Ready(state);
        execution = Actions.CompleteCheckpoint(Actions.PlayLand(execution, A, forest));
        forest = Assert.Single(execution.State.Objects.Values, o => o.Definition.OracleId == "forest").Id;
        var physical = state.Objects[bear].CardInstanceId;
        execution = Actions.CompleteCheckpoint(Actions.Cast(execution, A,
            new(bear, new(0, 1, 0, 0, 1, 0), ManaSources: [forest, island])));
        var spell = Assert.Single(execution.State.In(ZoneKey.Shared(Zone.Stack)));
        Assert.Equal(ManaPool.Empty, execution.State.Player(A).Mana);
        Assert.True(execution.State.Objects[forest].Tapped);
        Assert.Throws<InvalidOperationException>(() => Actions.Cast(execution, A, new(bear, ManaPool.Empty)));
        execution = Round(execution);
        Assert.Equal(spell, execution.Resolving);
        execution = Actions.CompleteCheckpoint(Actions.Resolve(execution));
        var creature = Assert.Single(execution.State.Objects.Values, o => o.Definition.OracleId == "bear");
        Assert.Equal(Zone.Battlefield, creature.Zone.Zone);
        Assert.True(creature.SummoningSick);
        Assert.Equal(physical, creature.CardInstanceId);
        Assert.NotEqual(spell, creature.Id);
        Assert.Equal(A, execution.State.Priority.Holder);
    }

    [Fact]
    public void Failed_payment_rolls_back_mana_taps_zone_changes_and_passes()
    {
        var (state, island) = Add(Setup(), Island, A, Zone.Battlefield);
        (state, var bear) = Add(state, Bear, A, Zone.Hand);
        var execution = Ready(state);
        Assert.Throws<InvalidOperationException>(() => Actions.Cast(execution, A,
            new(bear, new(0, 1, 0, 0, 1, 0), ManaSources: [island])));
        Assert.Same(state.Objects[island].Definition, execution.State.Objects[island].Definition);
        Assert.False(execution.State.Objects[island].Tapped);
        Assert.Equal(ManaPool.Empty, execution.State.Player(A).Mana);
        Assert.Contains(bear, execution.State.In(ZoneKey.Of(Zone.Hand, A)));
        Assert.Empty(execution.State.In(ZoneKey.Shared(Zone.Stack)));
        Assert.Equal(ExecutionBoundary.Priority, execution.Boundary);
    }

    [Fact]
    public void Mana_is_immediate_and_an_intervening_activation_resets_passes()
    {
        var (state, island) = Add(Setup(), Island, B, Zone.Battlefield);
        var execution = Actions.CompleteCheckpoint(Ready(state).Pass(A));
        Assert.Single(execution.State.Priority.PassedInSuccession);
        execution = Actions.CompleteCheckpoint(Actions.ActivateMana(execution, B, island));
        Assert.Empty(execution.State.Priority.PassedInSuccession);
        Assert.Equal(B, execution.State.Priority.Holder);
        Assert.Equal(1, execution.State.Player(B).Mana.Blue);
        Assert.Empty(execution.State.In(ZoneKey.Shared(Zone.Stack)));
        Assert.Throws<InvalidOperationException>(() => Actions.ActivateMana(execution, B, island));
    }

    [Fact]
    public void Duplicate_or_foreign_mana_sources_and_wrong_actor_are_rejected()
    {
        var (state, island) = Add(Setup(), Island, B, Zone.Battlefield);
        (state, var life) = Add(state, Life, A, Zone.Hand);
        var execution = Ready(state);
        Assert.Throws<InvalidOperationException>(() => Actions.Cast(execution, B, new(life, ManaPool.Empty, TargetPlayer: B)));
        Assert.Throws<InvalidOperationException>(() => Actions.Cast(execution, A,
            new(life, new(0, 1, 0, 0, 0, 0), TargetPlayer: B, ManaSources: [island])));
        Assert.Throws<InvalidOperationException>(() => Actions.Cast(execution, A,
            new(life, new(0, 2, 0, 0, 0, 0), TargetPlayer: B, ManaSources: [island, island])));
        Assert.False(execution.State.Objects[island].Tapped);
    }

    [Fact]
    public void Response_bounces_target_and_earlier_spell_does_not_follow_physical_card()
    {
        var (state, creature) = Add(Setup(), Bear, A, Zone.Battlefield);
        (state, var first) = Add(state, Bounce, A, Zone.Hand);
        (state, var second) = Add(state, Bounce, B, Zone.Hand);
        (state, var landA) = Add(state, Island, A, Zone.Battlefield);
        (state, var landB) = Add(state, Island, B, Zone.Battlefield);
        var execution = Ready(state);
        execution = Actions.CompleteCheckpoint(Actions.Cast(execution, A,
            new(first, new(0, 1, 0, 0, 0, 0), creature, ManaSources: [landA])));
        var firstSpell = execution.State.In(ZoneKey.Shared(Zone.Stack))[0];
        execution = Actions.CompleteCheckpoint(execution.Pass(A));
        execution = Actions.CompleteCheckpoint(Actions.Cast(execution, B,
            new(second, new(0, 1, 0, 0, 0, 0), creature, ManaSources: [landB])));
        Assert.Empty(execution.State.Priority.PassedInSuccession);
        execution = Actions.CompleteCheckpoint(Actions.Resolve(Round(execution)));
        Assert.Equal(firstSpell, Assert.Single(execution.State.In(ZoneKey.Shared(Zone.Stack))));
        var returned = Assert.Single(execution.State.In(ZoneKey.Of(Zone.Hand, A)));
        Assert.NotEqual(creature, returned);
        Assert.Equal(state.Objects[creature].CardInstanceId, execution.State.Objects[returned].CardInstanceId);
        execution = Actions.CompleteCheckpoint(Actions.Resolve(Round(execution)));
        Assert.Equal(returned, Assert.Single(execution.State.In(ZoneKey.Of(Zone.Hand, A))));
        Assert.Empty(execution.State.In(ZoneKey.Shared(Zone.Stack)));
        Assert.Single(execution.State.In(ZoneKey.Of(Zone.Graveyard, A)));
        Assert.Single(execution.State.In(ZoneKey.Of(Zone.Graveyard, B)));
    }

    [Fact]
    public void Target_player_choice_is_explicit_and_effect_waits_for_resolution()
    {
        var (state, spell) = Add(Setup(), Life, A, Zone.Hand);
        (state, var forest) = Add(state, Forest, A, Zone.Battlefield);
        var execution = Ready(state);
        Assert.Throws<InvalidOperationException>(() => Actions.Cast(execution, A, new(spell, ManaPool.Empty)));
        Assert.Throws<InvalidOperationException>(() => Actions.Cast(execution, A, new(spell, ManaPool.Empty, TargetPlayer: new(99))));
        execution = Actions.CompleteCheckpoint(Actions.Cast(execution, A,
            new(spell, new(0, 0, 0, 0, 1, 0), TargetPlayer: B, ManaSources: [forest])));
        Assert.Equal(20, execution.State.Player(B).Life);
        execution = Actions.CompleteCheckpoint(Actions.Resolve(Round(execution)));
        Assert.Equal(23, execution.State.Player(B).Life);
        Assert.Equal(20, execution.State.Player(A).Life);
    }

    [Theory]
    [InlineData("{X}{G}")]
    [InlineData("{G/U}")]
    [InlineData("{G/P}")]
    [InlineData("{S}")]
    [InlineData(null)]
    public void Unsupported_costs_are_rejected_at_registration(string? cost) => Assert.Throws<NotSupportedException>(() =>
        new BasicActions([new(Card("unsupported", CardType.Creature, cost), BasicBehavior.Creature)]));

    [Fact]
    public void Unimplemented_text_cannot_be_registered_as_a_vanilla_creature() => Assert.Throws<NotSupportedException>(() =>
        new BasicActions([new(Card("flying", CardType.Creature, "{G}", "Flying"), BasicBehavior.Creature)]));

    [Fact]
    public void Mana_payment_requires_color_and_exact_total_including_colorless()
    {
        var pool = new ManaPool(0, 2, 0, 0, 1, 1);
        Assert.Equal(new ManaPool(0, 1, 0, 0, 0, 0), BasicMana.Pay(pool, new(0, 1, 0, 0, 1, 1), "{1}{G}{C}"));
        Assert.Throws<InvalidOperationException>(() => BasicMana.Pay(pool, new(0, 2, 0, 0, 1, 0), "{1}{G}{C}"));
        Assert.Throws<InvalidOperationException>(() => BasicMana.Pay(pool, pool, "{1}{G}{C}"));
        Assert.Throws<InvalidOperationException>(() => BasicMana.Pay(pool, new(0, -1, 0, 0, 0, 0), "{0}"));
        Assert.Equal(pool, BasicMana.Pay(pool, ManaPool.Empty, "{0}"));
    }

    [Fact]
    public void Rejected_response_preserves_the_existing_pass_sequence()
    {
        var (state, life) = Add(Setup(), Life, B, Zone.Hand);
        var execution = Actions.CompleteCheckpoint(Ready(state).Pass(A));
        Assert.Throws<InvalidOperationException>(() => Actions.Cast(execution, B,
            new(life, new(1, 0, 0, 0, 0, 0), TargetPlayer: A)));
        Assert.Equal([A], execution.State.Priority.PassedInSuccession);
        Assert.Equal(B, execution.State.Priority.Holder);
    }

    [Fact]
    public void Lands_and_creatures_require_active_main_phase_and_empty_stack()
    {
        var (state, land) = Add(Setup(), Forest, A, Zone.Hand);
        (state, var bear) = Add(state, Bear, A, Zone.Hand);
        var upkeep = Actions.CompleteCheckpoint(TurnExecution.Start(state, A).CompleteBasicTurnAction());
        Assert.Throws<InvalidOperationException>(() => Actions.PlayLand(upkeep, A, land));
        Assert.Throws<InvalidOperationException>(() => Actions.Cast(upkeep, A, new(bear, ManaPool.Empty)));
        var opponent = Actions.CompleteCheckpoint(Ready(state).Pass(A));
        Assert.Throws<InvalidOperationException>(() => Actions.PlayLand(opponent, B, land));
        Assert.Throws<InvalidOperationException>(() => Actions.Cast(opponent, B, new(bear, ManaPool.Empty)));

        (state, var life) = Add(state, Life, A, Zone.Hand);
        (state, var source) = Add(state, Island, A, Zone.Battlefield);
        var stacked = Actions.CompleteCheckpoint(Actions.Cast(Ready(state), A,
            new(life, new(0, 1, 0, 0, 0, 0), TargetPlayer: A, ManaSources: [source])));
        Assert.Throws<InvalidOperationException>(() => Actions.PlayLand(stacked, A, land));
        Assert.Throws<InvalidOperationException>(() => Actions.Cast(stacked, A, new(bear, ManaPool.Empty)));
    }

    [Fact]
    public void Sorcery_targeted_spell_obeys_main_phase_timing()
    {
        var sorcery = Card("sorcery", CardType.Sorcery, "{0}", "Target player gains 3 life.");
        var actions = new BasicActions([new(sorcery, BasicBehavior.GainLife, 3)]);
        var (state, id) = Add(Setup(), sorcery, A, Zone.Hand);
        var execution = actions.CompleteCheckpoint(TurnExecution.Start(state, A).CompleteBasicTurnAction());
        Assert.Throws<InvalidOperationException>(() => actions.Cast(execution, A, new(id, ManaPool.Empty, TargetPlayer: A)));
        execution = actions.CompleteCheckpoint(execution.Pass(A));
        execution = actions.CompleteCheckpoint(execution.Pass(B));
        execution = actions.CompleteCheckpoint(actions.Cast(execution, A, new(id, ManaPool.Empty, TargetPlayer: B)));
        execution = actions.CompleteCheckpoint(execution.Pass(A));
        execution = actions.CompleteCheckpoint(actions.Resolve(execution.Pass(B)));
        Assert.Equal(23, execution.State.Player(B).Life);
    }

    [Fact]
    public void Returning_a_creature_hides_its_new_object_identity_from_opponents()
    {
        var (state, creature) = Add(Setup(), Bear, A, Zone.Battlefield);
        (state, var bounce) = Add(state, Bounce, A, Zone.Hand);
        (state, var land) = Add(state, Island, A, Zone.Battlefield);
        var execution = Actions.CompleteCheckpoint(Actions.Cast(Ready(state), A,
            new(bounce, new(0, 1, 0, 0, 0, 0), creature, ManaSources: [land])));
        execution = Actions.CompleteCheckpoint(Actions.Resolve(Round(execution)));
        var view = ZoneStateView.For(execution.State, B);
        var hand = Assert.Single(view.Zones, z => z.Key == ZoneKey.Of(Zone.Hand, A));
        Assert.Equal(1, hand.Count);
        Assert.Empty(hand.Objects);
        Assert.DoesNotContain(view.Zones.SelectMany(z => z.Objects), o => o.Id == creature);
    }

    [Fact]
    public void Target_requires_a_current_battlefield_creature_and_exact_target_shape()
    {
        var (state, creature) = Add(Setup(), Bear, A, Zone.Hand);
        (state, var bounce) = Add(state, Bounce, A, Zone.Hand);
        (state, var land) = Add(state, Island, A, Zone.Battlefield);
        var execution = Ready(state);
        foreach (var target in new[] { creature, land, new ObjectId(999) })
            Assert.Throws<InvalidOperationException>(() => Actions.Cast(execution, A,
                new(bounce, new(0, 1, 0, 0, 0, 0), target, ManaSources: [land])));
        Assert.Throws<InvalidOperationException>(() => Actions.Cast(execution, A,
            new(bounce, ManaPool.Empty, TargetPlayer: B)));
        Assert.False(execution.State.Objects[land].Tapped);
    }

    [Fact]
    public void Registry_rejects_changed_text_even_when_oracle_identity_matches()
    {
        var altered = Bear with { Faces = [Bear.Faces[0] with { OracleText = "Flying" }] };
        var (state, _) = Add(Setup(), altered, A, Zone.Hand);
        var execution = TurnExecution.Start(state, A).CompleteBasicTurnAction();
        Assert.Throws<NotSupportedException>(() => Actions.CompleteCheckpoint(execution));
    }

    [Fact]
    public void Checkpoint_rejects_a_state_requiring_unimplemented_state_based_actions()
    {
        var (state, bear) = Add(Setup(), Bear, A, Zone.Battlefield);
        state = state with { Objects = state.Objects.SetItem(bear, state.Objects[bear] with { Damage = 2 }) };
        var execution = TurnExecution.Start(state, A).CompleteBasicTurnAction();
        Assert.Throws<NotSupportedException>(() => Actions.CompleteCheckpoint(execution));
    }
}
