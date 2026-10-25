using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Turn;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Turn structure, CR 500–514: the steps in order, the turn-based actions of CR 703.4 the turn
/// system owns, and who takes the next turn. The rulebook's own statements are the acceptance
/// tests: CR 103.8a's skipped draw, CR 514.1's discard, CR 514.2's damage and "until end of
/// turn" effects, CR 500.7's extra turn.
/// </summary>
public sealed partial class TurnStructureTests
{
    private readonly TurnFixture.FakeZones _zones = new();
    private readonly TurnFixture.FakeCasting _casting = new();

    private TurnSystem Turns => new(_zones, _casting);

    [Fact]
    public void Begin_starts_turn_one_for_the_player_at_turn_order_index_zero()
    {
        var outcome = Turns.Begin(TurnFixture.Table(2));

        var began = Assert.Single(outcome.Events.OfType<TurnBegan>());
        Assert.Equal(1, began.Number);
        Assert.Equal(TurnFixture.P0, began.ActivePlayer);
        Assert.False(began.Extra);
        Assert.Equal(1, outcome.State.Turn.Number);
    }

    [Fact]
    public void Begin_runs_the_untap_step_and_stops_in_the_upkeep_step()
    {
        // CR 501.1: the beginning phase is untap, upkeep, draw. CR 502.4: nobody receives
        // priority in the untap step, so the first thing anyone is asked is in the upkeep step.
        var outcome = Turns.Begin(TurnFixture.Table(2));

        Assert.Equal(new[] { Step.Untap, Step.Upkeep }, TurnFixture.StepsIn(outcome.Events));
        Assert.Equal(Step.Upkeep, outcome.State.Turn.Step);
        Assert.Equal(TurnFixture.P0, outcome.State.Priority.Holder);
    }

    [Fact]
    public void A_whole_turn_walks_every_step_of_500_1_in_order_for_two_players()
    {
        var (state, log) = Turns.PassUntilTurn(TurnFixture.Table(2), untilTurn: 2);

        Assert.Equal(new List<Step>(TurnFixture.TurnWithNoCombat.Where(step => step != Step.Draw)) { Step.Untap, Step.Upkeep }, TurnFixture.StepsIn(log));
        Assert.Equal(new[] { TurnFixture.P0, TurnFixture.P1 }, log.OfType<TurnBegan>().Select(e => e.ActivePlayer));
        Assert.Equal(2, state.Turn.Number);
    }

    [Fact]
    public void A_whole_turn_walks_every_step_of_500_1_in_order_for_four_players()
    {
        var (state, log) = Turns.PassUntilTurn(TurnFixture.Table(4), untilTurn: 2);

        Assert.Equal(new List<Step>(TurnFixture.TurnWithNoCombat) { Step.Untap, Step.Upkeep }, TurnFixture.StepsIn(log));
        Assert.Equal(TurnFixture.P1, state.Turn.ActivePlayer);
        Assert.Equal(2, state.Turn.Number);
    }

    [Fact]
    public void The_starting_player_skips_the_draw_step_in_a_two_player_game()
    {
        // CR 103.8a.
        var (state, _) = Turns.PassUntilTurn(TurnFixture.Table(2), untilTurn: 2);

        Assert.Empty(_zones.Draws);
        Assert.Equal(2, state.Turn.Number);
    }

    [Fact]
    public void No_player_skips_the_draw_step_in_a_four_player_game()
    {
        // CR 103.8c.
        Turns.PassUntilTurn(TurnFixture.Table(4), untilTurn: 2);

        Assert.Equal((TurnFixture.P0, 1), Assert.Single(_zones.Draws));
    }

    [Fact]
    public void The_second_turn_of_a_two_player_game_draws()
    {
        // CR 504.1: only the *first* turn of the game skips the draw, and only for the player
        // who played first.
        Turns.PassUntilTurn(TurnFixture.Table(2), untilTurn: 3);

        Assert.Equal((TurnFixture.P1, 1), Assert.Single(_zones.Draws));
    }

    [Fact]
    public void The_untap_step_untaps_the_active_players_permanents_and_clears_summoning_sickness()
    {
        // CR 502.3 and CR 302.6.
        var state = TurnFixture.Table(2).With(
            TurnFixture.Permanent(1, TurnFixture.P0, tapped: true, sick: true),
            TurnFixture.Permanent(2, TurnFixture.P1, tapped: true, sick: true));

        var outcome = Turns.Begin(state);

        var changed = Assert.Single(outcome.Events.OfType<ObjectChanged>());
        Assert.Equal(new ObjectId(1), changed.Id);
        Assert.False(changed.Tapped);
        Assert.False(changed.SummoningSick);
        Assert.False(outcome.State.Objects[new ObjectId(1)].Tapped);
        Assert.False(outcome.State.Objects[new ObjectId(1)].SummoningSick);
        Assert.True(outcome.State.Objects[new ObjectId(2)].Tapped);
        Assert.True(outcome.State.Objects[new ObjectId(2)].SummoningSick);
    }

    [Fact]
    public void Mana_pools_empty_as_each_step_ends()
    {
        // CR 500.5 and CR 703.4q.
        var state = TurnFixture.Table(2).At(Step.PrecombatMain, TurnFixture.P0);
        state = state with { Players = [.. state.Players.Select(p => p with { Mana = new ManaPool(1, 0, 0, 0, 0, 2) })] };

        var outcome = Turns.Advance(state);

        Assert.Equal(new[] { TurnFixture.P0, TurnFixture.P1 }, outcome.Events.OfType<ManaPoolEmptied>().Select(e => e.Player));
        Assert.All(outcome.State.Players, p => Assert.Equal(0, p.Mana.Total));
    }

    [Fact]
    public void Cleanup_with_eight_cards_in_hand_asks_the_active_player_to_discard()
    {
        // CR 514.1: the maximum hand size is normally seven.
        var state = TurnFixture.Table(2).At(Step.End, TurnFixture.P0)
            .With([.. Enumerable.Range(1, 8).Select(i => TurnFixture.InHand(i, TurnFixture.P0))]);

        var outcome = Turns.Advance(state);

        var choice = Assert.Single(outcome.Events.OfType<ChoiceRequested>()).Choice;
        Assert.Equal("discard", choice.Kind);
        Assert.Equal(TurnFixture.P0, choice.Player);
        Assert.Equal(1, choice.Min);
        Assert.Equal(1, choice.Max);
        Assert.Equal(8, choice.Options.Count);

        // The step stopped there: no damage was cleared and no turn began, CR 101.4.
        Assert.Equal(Step.Cleanup, outcome.State.Turn.Step);
        Assert.Empty(outcome.Events.OfType<TurnBegan>());
        Assert.NotNull(outcome.State.Pending);
    }

    [Fact]
    public void Cleanup_with_a_hand_at_the_maximum_asks_nothing()
    {
        var state = TurnFixture.Table(2).At(Step.End, TurnFixture.P0)
            .With([.. Enumerable.Range(1, 7).Select(i => TurnFixture.InHand(i, TurnFixture.P0))]);

        var outcome = Turns.Advance(state);

        Assert.Empty(outcome.Events.OfType<ChoiceRequested>());
        Assert.Equal(2, outcome.State.Turn.Number);
    }

    [Fact]
    public void The_cleanup_step_finishes_and_the_next_turn_begins_once_the_discard_is_answered()
    {
        var state = TurnFixture.Table(2).At(Step.End, TurnFixture.P0)
            .With([.. Enumerable.Range(1, 8).Select(i => TurnFixture.InHand(i, TurnFixture.P0))]);
        var asked = Turns.Advance(state);

        // Answering is the choice system's; what the turn system owes is the rest of CR 514.
        var outcome = Turns.Advance(asked.State with { Pending = null });

        Assert.Equal(2, outcome.State.Turn.Number);
        Assert.Equal(TurnFixture.P1, outcome.State.Turn.ActivePlayer);
    }

    [Fact]
    public void Damage_is_removed_and_until_end_of_turn_effects_end_at_cleanup()
    {
        // CR 514.2.
        var state = TurnFixture.Table(2).At(Step.End, TurnFixture.P0)
            .With(TurnFixture.Permanent(1, TurnFixture.P0, damage: 3));
        state = state with
        {
            ActiveEffects = [TurnFixture.Effect(1, new UntilEndOfTurn()), TurnFixture.Effect(2, new Permanently())],
        };

        var outcome = Turns.Advance(state);

        Assert.Contains(outcome.Events.OfType<ObjectChanged>(), e => e.Id == new ObjectId(1) && e.Damage == 0);
        Assert.Equal(new[] { 1 }, Assert.Single(outcome.Events.OfType<EndOfTurnEffectsEnded>()).Effects);
        Assert.Equal(0, outcome.State.Objects[new ObjectId(1)].Damage);
        Assert.Equal(new[] { 2 }, outcome.State.ActiveEffects.Select(e => e.Id));
    }

    [Fact]
    public void An_extra_turn_is_taken_before_the_next_players_turn()
    {
        // CR 500.7.
        var state = TurnFixture.Table(2).At(Step.End, TurnFixture.P0);
        state = state with { Turn = state.Turn with { ExtraTurns = [TurnFixture.P0] } };

        var extra = Turns.Advance(state);

        var began = Assert.Single(extra.Events.OfType<TurnBegan>());
        Assert.Equal(TurnFixture.P0, began.ActivePlayer);
        Assert.True(began.Extra);
        Assert.Equal(2, began.Number);
        Assert.Empty(extra.State.Turn.ExtraTurns);

        // And the turn after the extra one is the turn that was waiting.
        var after = Turns.Advance(extra.State.At(Step.End, TurnFixture.P0, number: 2));

        Assert.Equal(TurnFixture.P1, Assert.Single(after.Events.OfType<TurnBegan>()).ActivePlayer);
        Assert.Equal(3, after.State.Turn.Number);
    }

    [Fact]
    public void A_player_who_has_lost_takes_no_turn()
    {
        // CR 104.3: a player who leaves the game is no longer in the turn order.
        var state = TurnFixture.Table(3).At(Step.End, TurnFixture.P0);
        state = new PlayerLost(TurnFixture.P1, "test").Apply(state);

        var outcome = Turns.Advance(state);

        Assert.Equal(TurnFixture.P2, Assert.Single(outcome.Events.OfType<TurnBegan>()).ActivePlayer);
    }

    [Fact]
    public void Advance_does_nothing_once_the_game_is_over()
    {
        // CR 104.1: the game ends immediately.
        var state = TurnFixture.Table(2).At(Step.End, TurnFixture.P0);
        state = new GameEnded(new GameOutcome([TurnFixture.P0], Draw: false, "test")).Apply(state);

        var outcome = Turns.Advance(state);

        Assert.Empty(outcome.Events);
        Assert.Equal(Step.End, outcome.State.Turn.Step);
    }

    [Fact]
    [RuleRef("703.4m")]
    public void Combat_damage_is_dealt_as_each_combat_damage_step_begins()
    {
        // CR 510.1 and CR 703.4m: assigning and dealing combat damage is a turn-based action at
        // the beginning of the step, so it happens before anyone receives priority in it.
        var spy = new CombatSpy();
        var turns = new TurnSystem(_zones, _casting, spy);

        var combat = TurnFixture.Table(2).At(Step.DeclareBlockers, TurnFixture.P0).InCombat(TurnFixture.P0, new ObjectId(1));
        var first = turns.Advance(combat);
        var second = turns.Advance(first.State);

        Assert.Equal<bool>([true, false], spy.Dealt);
        Assert.Single(first.Events.OfType<DamageDealt>());
        Assert.Single(second.Events.OfType<DamageDealt>());

        // Before priority: the step began, damage was dealt, and only then was priority granted.
        var order = second.Events.Select(e => e.GetType().Name).ToList();
        Assert.True(order.IndexOf(nameof(StepBegan)) < order.IndexOf(nameof(DamageDealt)));
        Assert.True(order.IndexOf(nameof(DamageDealt)) < order.IndexOf(nameof(PriorityGranted)));
    }

    [Fact]
    [RuleRef("511.3")]
    public void Creatures_are_removed_from_combat_as_the_end_of_combat_step_ends()
    {
        var spy = new CombatSpy();
        var turns = new TurnSystem(_zones, _casting, spy);

        var outcome = turns.Advance(TurnFixture.Table(2).At(Step.EndOfCombat, TurnFixture.P0));

        Assert.Equal(1, spy.Ended);
        Assert.Single(outcome.Events.OfType<CombatEnded>());
        Assert.Equal(Step.PostcombatMain, outcome.State.Turn.Step);
    }

    [Fact]
    [RuleRef("510")]
    public void A_turn_system_built_without_a_combat_system_deals_no_damage()
    {
        // The optional seam, stated as behaviour: the steps still happen and nothing is dealt.
        var combat = TurnFixture.Table(2).At(Step.DeclareBlockers, TurnFixture.P0).InCombat(TurnFixture.P0, new ObjectId(1));

        var outcome = Turns.Advance(combat);

        Assert.Equal(Step.FirstStrikeCombatDamage, outcome.State.Turn.Step);
        Assert.Empty(outcome.Events.OfType<DamageDealt>());
    }

    [Fact]
    [RuleRef("510.4")]
    public void There_is_no_first_strike_damage_step_when_nobody_in_combat_has_first_strike()
    {
        // CR 510.4: the first-strike combat damage step exists only for first and double strike.
        // With the layer system to ask, an ordinary attacker means one damage step, so the
        // defending player is not given a round of priority in a step that should not exist.
        var layers = new CombatFakeCharacteristics();
        var attacker = new ObjectId(1);
        layers.Set(attacker, TurnFixture.Creature(TurnFixture.P0));
        var spy = new CombatSpy();
        var turns = new TurnSystem(_zones, _casting, spy, layers);
        var state = TurnFixture.Table(2)
            .With(TurnFixture.Permanent(1, TurnFixture.P0, tapped: true))
            .At(Step.DeclareBlockers, TurnFixture.P0)
            .InCombat(TurnFixture.P0, attacker);

        var outcome = turns.Advance(state);

        Assert.Equal(Step.CombatDamage, outcome.State.Turn.Step);
        Assert.Equal<bool>([false], spy.Dealt);
    }

    [Theory]
    [InlineData(KeywordKind.FirstStrike)]
    [InlineData(KeywordKind.DoubleStrike)]
    [RuleRef("510.4")]
    public void The_first_strike_damage_step_happens_when_a_creature_in_combat_has_it(KeywordKind keyword)
    {
        var layers = new CombatFakeCharacteristics();
        var attacker = new ObjectId(1);
        layers.Set(attacker, TurnFixture.Creature(TurnFixture.P0, keyword));
        var spy = new CombatSpy();
        var turns = new TurnSystem(_zones, _casting, spy, layers);
        var state = TurnFixture.Table(2)
            .With(TurnFixture.Permanent(1, TurnFixture.P0, tapped: true))
            .At(Step.DeclareBlockers, TurnFixture.P0)
            .InCombat(TurnFixture.P0, attacker);

        var outcome = turns.Advance(state);

        Assert.Equal(Step.FirstStrikeCombatDamage, outcome.State.Turn.Step);
        Assert.Equal<bool>([true], spy.Dealt);
    }

    [Fact]
    [RuleRef("502.3")]
    public void Untap_untaps_the_permanents_the_active_player_controls_now()
    {
        // CR 502.3: the active player untaps the permanents *they control*, and control is what
        // the layer system computes, CR 613.1 — not who the permanent was dealt to. A creature
        // whose control changed untaps in its new controller's untap step and not its old one's.
        var layers = new CombatFakeCharacteristics();
        var stolen = new ObjectId(1);
        var theirs = new ObjectId(2);
        layers.Set(stolen, TurnFixture.Creature(TurnFixture.P0));
        layers.Set(theirs, TurnFixture.Creature(TurnFixture.P1));
        var turns = new TurnSystem(_zones, _casting, null, layers);
        var state = TurnFixture.Table(2)
            .With(TurnFixture.Permanent(1, TurnFixture.P1, tapped: true), TurnFixture.Permanent(2, TurnFixture.P1, tapped: true));

        var outcome = turns.Begin(state);

        var untapped = outcome.Events.OfType<ObjectChanged>().Where(e => e.Tapped == false).Select(e => e.Id);
        Assert.Equal([stolen], untapped);
        Assert.False(outcome.State.Objects[stolen].Tapped);
        Assert.True(outcome.State.Objects[theirs].Tapped);
    }

    /// <summary>
    /// Combat as the turn system sees it: it records which damage step it was asked for and emits
    /// one damage event, so "the turn-based action ran here" is what the test reads, not how much
    /// damage a creature assigns, which is <c>CombatTests</c>'s.
    /// </summary>
    private sealed class CombatSpy : ICombatSystem
    {
        public List<bool> Dealt { get; } = [];

        public int Ended { get; private set; }

        public string Name => "combat spy";

        public Outcome DeclareAttackers(GameState state, Rules.Commands.DeclareAttackers command) => throw new NotSupportedException();

        public Outcome DeclareBlockers(GameState state, Rules.Commands.DeclareBlockers command) => throw new NotSupportedException();

        public Outcome DealCombatDamage(GameState state, bool firstStrike)
        {
            Dealt.Add(firstStrike);
            ImmutableList<GameEvent> events = [new DamageDealt(new ObjectId(1), null, TurnFixture.P1, 1, Combat: true, FromCommander: false)];
            return new Outcome(GameReducer.Apply(state, events), events);
        }

        public Outcome EndCombat(GameState state)
        {
            Ended++;
            ImmutableList<GameEvent> events = [new CombatEnded()];
            return new Outcome(GameReducer.Apply(state, events), events);
        }
    }
}
