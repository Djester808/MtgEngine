using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 24's turn rules: the monarch's draw at the beginning of their end step (CR 724.3), the
/// cleanup step dropping a delayed triggered ability dated to this turn (CR 603.7c with 514.2),
/// and the per-step and per-turn records CR 121.1 asks the state to keep.
/// </summary>
public sealed partial class TurnStructureTests
{
    /// <summary>A land with no abilities: enough to be something that enters the battlefield as a land, CR 305.1.</summary>
    private static CardDefinition Wave24Land { get; } = new(
        "00000000-0000-0000-0000-000000000024",
        "Test Land",
        Layout.Normal,
        [new Face("Test Land", FaceRole.Main, new TypeLine(CardType.Land))]);

    private static DelayedTriggerState Wave24Delayed(int id, Duration? until) => new(
        id,
        new AtStep(Step.End, new You()),
        new GainLife(new Fixed(1)),
        null,
        TurnFixture.P0,
        Once: false,
        ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
        until);

    [Fact]
    [RuleRef("724.3")]
    public void The_monarch_draws_a_card_at_the_beginning_of_their_end_step()
    {
        var state = TurnFixture.Table(2).At(Step.PostcombatMain, TurnFixture.P0) with { Monarch = TurnFixture.P0 };

        var outcome = Turns.Advance(state);

        Assert.Equal(Step.End, outcome.State.Turn.Step);
        Assert.Equal((TurnFixture.P0, 1), Assert.Single(_zones.Draws));
    }

    [Fact]
    [RuleRef("724.3")]
    public void The_monarch_draws_nothing_in_another_players_end_step()
    {
        // CR 724.3 gives the monarch "at the beginning of *your* end step", so the end step of a turn
        // somebody else is taking draws them nothing.
        var state = TurnFixture.Table(2).At(Step.PostcombatMain, TurnFixture.P0) with { Monarch = TurnFixture.P1 };

        var outcome = Turns.Advance(state);

        Assert.Equal(Step.End, outcome.State.Turn.Step);
        Assert.Empty(_zones.Draws);
    }

    [Fact]
    [RuleRef("724.1")]
    public void An_end_step_with_no_monarch_draws_nobody_a_card()
    {
        var outcome = Turns.Advance(TurnFixture.Table(2).At(Step.PostcombatMain, TurnFixture.P0));

        Assert.Equal(Step.End, outcome.State.Turn.Step);
        Assert.Empty(_zones.Draws);
    }

    [Fact]
    [RuleRef("104.3")]
    public void A_monarch_who_has_lost_the_game_draws_nothing()
    {
        var state = TurnFixture.Table(2).At(Step.PostcombatMain, TurnFixture.P0) with { Monarch = TurnFixture.P0 };
        state = state with { Players = state.Players.SetItem(0, state.Player(TurnFixture.P0) with { HasLost = true }) };

        var outcome = Turns.Advance(state);

        Assert.Empty(_zones.Draws);
    }

    [Fact]
    [RuleRef("603.7c")]
    public void Cleanup_drops_a_delayed_trigger_dated_to_this_turn()
    {
        var state = TurnFixture.Table(2).At(Step.End, TurnFixture.P0) with
        {
            DelayedTriggers = [Wave24Delayed(1, new UntilEndOfTurn())],
        };

        var outcome = Turns.Advance(state);

        Assert.Equal(new[] { 1 }, Assert.Single(outcome.Events.OfType<EndOfTurnEffectsEnded>()).DelayedTriggers);
        Assert.Empty(outcome.State.DelayedTriggers);
    }

    [Fact]
    [RuleRef("603.7c")]
    public void Cleanup_keeps_a_delayed_trigger_that_has_no_duration_or_a_later_one()
    {
        // A delayed trigger waiting for its event with no deadline, and one dated to a window this
        // turn does not close: dropping either here would delete an ability that can still fire.
        var state = TurnFixture.Table(2).At(Step.End, TurnFixture.P0) with
        {
            DelayedTriggers = [Wave24Delayed(1, null), Wave24Delayed(2, new UntilYourNextTurn())],
        };

        var outcome = Turns.Advance(state);

        Assert.Empty(outcome.Events.OfType<EndOfTurnEffectsEnded>());
        Assert.Equal(new[] { 1, 2 }, outcome.State.DelayedTriggers.Select(t => t.Id));
    }

    [Fact]
    [RuleRef("514.2")]
    public void Cleanup_ends_an_until_end_of_turn_effect_and_a_dated_delayed_trigger_in_one_event()
    {
        var state = TurnFixture.Table(2).At(Step.End, TurnFixture.P0) with
        {
            ActiveEffects = [TurnFixture.Effect(7, new UntilEndOfTurn())],
            DelayedTriggers = [Wave24Delayed(3, new UntilEndOfTurn())],
        };

        var ended = Assert.Single(Turns.Advance(state).Events.OfType<EndOfTurnEffectsEnded>());

        Assert.Equal(new[] { 7 }, ended.Effects);
        Assert.Equal(new[] { 3 }, ended.DelayedTriggers);
    }

    [Fact]
    [RuleRef("121.1")]
    public void A_draw_raises_both_the_turns_count_and_the_steps_count()
    {
        var player = TurnFixture.Table(2).Player(TurnFixture.P0).Drew().Drew();

        Assert.Equal(2, player.ObjectsDrawnThisTurn);
        Assert.Equal(2, player.ObjectsDrawnThisStep);
    }

    [Fact]
    [RuleRef("500.1")]
    public void A_step_beginning_clears_the_steps_draw_count_and_keeps_the_turns()
    {
        var player = TurnFixture.Table(2).Player(TurnFixture.P0).Drew().StepBegins();

        Assert.Equal(1, player.ObjectsDrawnThisTurn);
        Assert.Equal(0, player.ObjectsDrawnThisStep);
    }

    [Fact]
    [RuleRef("121.1")]
    public void Cards_drawn_this_turn_is_read_per_player()
    {
        var state = TurnFixture.Table(2);
        state = new ObjectDrawn(TurnFixture.P0, new ObjectId(1)).Apply(state);
        state = new ObjectDrawn(TurnFixture.P0, new ObjectId(2)).Apply(state);

        Assert.Equal(2, state.CardsDrawnThisTurn(TurnFixture.P0));
        Assert.Equal(0, state.CardsDrawnThisTurn(TurnFixture.P1));
    }

    [Fact]
    [RuleRef("121.1")]
    public void A_land_that_entered_and_then_left_still_counts_this_turn()
    {
        var state = EnterLand(TurnFixture.Table(2), TurnFixture.P0, from: 1, to: 2);

        Assert.Equal(1, state.LandsEnteredThisTurn(TurnFixture.P0));
        Assert.Equal(0, state.LandsEnteredThisTurn(TurnFixture.P1));

        // CR 121.1 asks what entered, not what is there: destroying it does not un-enter it.
        var departed = state.Objects[new ObjectId(2)];
        state = new ObjectMoved(
            new ObjectId(2),
            departed with { Id = new ObjectId(3), Zone = ZoneKey.Of(Zone.Graveyard, TurnFixture.P0) },
            -1,
            departed).Apply(state);

        Assert.Equal(1, state.LandsEnteredThisTurn(TurnFixture.P0));
    }

    [Fact]
    [RuleRef("121.1")]
    public void A_creature_entering_is_not_a_land_entering()
    {
        var state = TurnFixture.Table(2);
        state = new ObjectMoved(new ObjectId(9), TurnFixture.Permanent(1, TurnFixture.P0), -1).Apply(state);

        Assert.Equal(0, state.LandsEnteredThisTurn(TurnFixture.P0));
    }

    [Fact]
    [RuleRef("111.1")]
    public void A_land_token_created_on_the_battlefield_counts_as_a_land_entering()
    {
        var token = new GameObject(new ObjectId(4), Wave24Land, 0, TurnFixture.P1, TurnFixture.P1, ZoneKey.Of(Zone.Battlefield, TurnFixture.P1), 4) { IsToken = true };
        var state = new ObjectCreated(token, -1).Apply(TurnFixture.Table(2));

        Assert.Equal(1, state.LandsEnteredThisTurn(TurnFixture.P1));
        Assert.Equal(0, state.LandsEnteredThisTurn(TurnFixture.P0));
    }

    [Fact]
    [RuleRef("121.1")]
    public void Both_turn_counts_are_cleared_as_the_next_turn_begins()
    {
        var state = EnterLand(TurnFixture.Table(2), TurnFixture.P0, from: 1, to: 2);
        state = new ObjectDrawn(TurnFixture.P0, new ObjectId(5)).Apply(state);

        state = new TurnBegan(2, TurnFixture.P1, Extra: false).Apply(state);

        Assert.Equal(0, state.LandsEnteredThisTurn(TurnFixture.P0));
        Assert.Equal(0, state.CardsDrawnThisTurn(TurnFixture.P0));
    }

    /// <summary>A land moves from its owner's hand onto the battlefield under their control, CR 305.1.</summary>
    private static GameState EnterLand(GameState state, PlayerId who, int from, int to)
    {
        var inHand = new GameObject(new ObjectId(from), Wave24Land, 0, who, who, ZoneKey.Of(Zone.Hand, who), from);
        state = new ObjectCreated(inHand, -1).Apply(state);
        return new ObjectMoved(
            new ObjectId(from),
            inHand with { Id = new ObjectId(to), Zone = ZoneKey.Of(Zone.Battlefield, who), Timestamp = to },
            -1).Apply(state);
    }
}
