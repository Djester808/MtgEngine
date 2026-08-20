using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The shape of a turn (CR 500–514) and the timing rules that hang off it.
/// </summary>
public sealed class TurnStructureTests
{
    [Fact]
    public void A_turn_runs_its_steps_in_order()
    {
        // CR 500.1. Walking the whole turn also proves the untap and cleanup steps do not
        // deadlock: neither grants priority, so neither is a place the game can rest.
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay(withMulligans: false);

        var seen = new List<TurnStep>();
        TestCards.PassUntil(game, () =>
        {
            if (!seen.Contains(game.State.CurrentStep))
                seen.Add(game.State.CurrentStep);

            return game.State.TurnNumber > 1;
        });

        // No creature attacked, so CR 506.1 skips the declare blockers and combat damage steps
        // entirely — they are not steps that happen and do nothing, they do not happen.
        Assert.Equal(
            [
                TurnStep.Upkeep, TurnStep.Draw, TurnStep.PrecombatMain,
                TurnStep.BeginningOfCombat, TurnStep.DeclareAttackers,
                TurnStep.EndOfCombat, TurnStep.PostcombatMain, TurnStep.End,
            ],
            seen);
        Assert.Equal(2, game.State.TurnNumber);
        Assert.Equal(bob, game.State.ActivePlayerId);
        Assert.NotEqual(alice, game.State.ActivePlayerId);
    }

    [Fact]
    public void Every_step_belongs_to_the_phase_the_rules_give_it()
    {
        // CR 500.1.
        Assert.Equal(Phase.Beginning, TurnStep.Upkeep.PhaseOf());
        Assert.Equal(Phase.PrecombatMain, TurnStep.PrecombatMain.PhaseOf());
        Assert.Equal(Phase.Combat, TurnStep.DeclareBlockers.PhaseOf());
        Assert.Equal(Phase.PostcombatMain, TurnStep.PostcombatMain.PhaseOf());
        Assert.Equal(Phase.Ending, TurnStep.Cleanup.PhaseOf());
    }

    [Fact]
    public void The_player_who_goes_first_skips_their_first_draw_step()
    {
        // CR 103.8a, in a two-player game only.
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay(withMulligans: false);

        Assert.Equal(7, game.State.GetPlayer(alice).Hand.Count);
        PriorityTests.PassTo(game, [alice, bob], TurnStep.PrecombatMain);

        Assert.Equal(7, game.State.GetPlayer(alice).Hand.Count);
    }

    [Fact]
    public void The_second_player_draws_on_their_first_turn()
    {
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay(withMulligans: false);

        while (game.State.TurnNumber == 1)
            game.PassPriority(game.State.Priority.Holder!.Value);
        PriorityTests.PassTo(game, [alice, bob], TurnStep.PrecombatMain);

        Assert.Equal(8, game.State.GetPlayer(bob).Hand.Count);
    }

    [Fact]
    public void Everyone_draws_on_their_first_turn_in_a_multiplayer_game()
    {
        // CR 103.8c: in all other multiplayer games, no player skips their first draw step.
        var (game, seats) = TestCards.MultiPlayer(4, deckSize: 40);
        game.BeginPlay(withMulligans: false);
        PriorityTests.PassTo(game, seats, TurnStep.PrecombatMain);

        Assert.Equal(8, game.State.GetPlayer(seats[0]).Hand.Count);
    }

    [Fact]
    public void The_active_players_permanents_untap_at_the_start_of_their_turn()
    {
        // CR 502.3, and only theirs.
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay(withMulligans: false);

        var mine = game.Create(alice, TestCards.Creature("Mine"), Zone.Battlefield);
        var theirs = game.Create(bob, TestCards.Creature("Theirs"), Zone.Battlefield);
        game.Tap(mine);
        game.Tap(theirs);

        while (game.State.TurnNumber == 1)
            game.PassPriority(game.State.Priority.Holder!.Value);

        // It is now Bob's turn, so Bob's untapped and Alice's did not.
        Assert.True(game.State.GetObject(mine).Permanent!.IsTapped);
        Assert.False(game.State.GetObject(theirs).Permanent!.IsTapped);
    }

    [Fact]
    public void Summoning_sickness_wears_off_when_your_turn_begins()
    {
        // CR 302.6.
        var (game, alice, _) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay(withMulligans: false);
        var creature = game.Create(alice, TestCards.Creature(), Zone.Battlefield);

        Assert.True(game.State.GetObject(creature).Permanent!.HasSummoningSickness);

        TestCards.PassToTurn(game, 3);

        Assert.False(game.State.GetObject(creature).Permanent!.HasSummoningSickness);
    }

    [Fact]
    public void Damage_is_removed_during_cleanup()
    {
        // CR 514.2.
        var (game, alice, _) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay(withMulligans: false);
        var creature = game.Create(alice, TestCards.Creature(), Zone.Battlefield);

        var damaged = game.State.GetObject(creature);
        Assert.NotNull(damaged.Permanent);

        while (game.State.TurnNumber == 1)
            game.PassPriority(game.State.Priority.Holder!.Value);

        Assert.Contains(game.Log, e => e is DamageCleared);
        Assert.Equal(0, game.State.GetObject(creature).Permanent!.DamageMarked);
    }

    [Fact]
    public void The_turn_does_not_end_while_someone_is_over_their_hand_size()
    {
        // CR 514.1. The engine will not choose the discard — that is the player's decision.
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay(withMulligans: false);
        for (var i = 0; i < 4; i++)
            TestCards.PutInHand(game, alice, TestCards.Creature($"Extra {i}"));

        PriorityTests.PassTo(game, [alice, bob], TurnStep.End);
        game.PassPriority(alice);
        game.PassPriority(bob);

        Assert.Equal(TurnStep.Cleanup, game.State.CurrentStep);
        Assert.Equal(1, game.State.TurnNumber);
        Assert.Equal([alice], game.PendingDiscards);
    }

    [Fact]
    public void Discarding_to_hand_size_lets_the_turn_end()
    {
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay(withMulligans: false);
        for (var i = 0; i < 4; i++)
            TestCards.PutInHand(game, alice, TestCards.Creature($"Extra {i}"));

        PriorityTests.PassTo(game, [alice, bob], TurnStep.End);
        game.PassPriority(alice);
        game.PassPriority(bob);

        while (game.PendingDiscards.Count > 0)
            game.Discard(alice, game.State.GetPlayer(alice).Hand[0]);

        Assert.Equal(2, game.State.TurnNumber);
        Assert.Equal(Game.MaxHandSize, game.State.GetPlayer(alice).Hand.Count);
    }

    [Fact]
    public void A_whole_turn_still_replays_to_the_same_state()
    {
        // The property from slice 1, now over the turn machinery: the log is still the game.
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay(withMulligans: false);
        PriorityTests.PassTo(game, [alice, bob], TurnStep.PrecombatMain);
        game.PlayLand(alice, TestCards.PutInHand(game, alice, TestCards.BasicLand()));
        var creature = TestCards.PutInHand(game, alice, TestCards.Creature());
        game.CastSpell(alice, creature);
        game.PassPriority(alice);
        game.PassPriority(bob);

        TestCards.PassToTurn(game, 3);

        Assert.Equal(game.State, GameReducer.Replay(game.Log));
    }
}
