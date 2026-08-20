using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Priority (CR 117), the rule the previous engine could not express.
/// </summary>
/// <remarks>
/// It asked "is the passer the active player?" and branched. That gives the right answer in a
/// duel by coincidence — the non-active player is the only other player — and no answer at all
/// at three. CR 117.4 is about all players passing <em>in succession</em>, so the engine has to
/// track who has passed since anything last happened. Every test here that runs at four players
/// is a test the old model could not have passed.
/// </remarks>
public sealed class PriorityTests
{
    [Fact]
    public void The_active_player_receives_priority_when_a_step_begins()
    {
        // CR 117.3a.
        var (game, alice, _) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);

        Assert.Equal(alice, game.State.Priority.Holder);
        Assert.Equal(TurnStep.Upkeep, game.State.CurrentStep);
    }

    [Fact]
    public void Nobody_has_priority_during_the_untap_step()
    {
        // CR 502.4. The game never rests there — it is walked through — so the assertion is
        // that the game is past it, having granted nobody priority on the way.
        var (game, _, _) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);

        Assert.NotEqual(TurnStep.Untap, game.State.CurrentStep);
        Assert.Contains(game.Log, e => e is Events.PriorityWithdrawn);
    }

    [Fact]
    public void Passing_hands_priority_to_the_next_player_in_turn_order()
    {
        // CR 117.3d.
        var (game, alice, bob) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);

        game.PassPriority(alice);

        Assert.Equal(bob, game.State.Priority.Holder);
    }

    [Fact]
    public void A_player_without_priority_cannot_pass()
    {
        var (game, _, bob) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);

        Assert.Throws<InvalidOperationException>(() => game.PassPriority(bob));
    }

    [Fact]
    public void All_players_passing_on_an_empty_stack_ends_the_step()
    {
        // CR 117.4 and 500.2.
        var (game, alice, bob) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);
        Assert.Equal(TurnStep.Upkeep, game.State.CurrentStep);

        game.PassPriority(alice);
        game.PassPriority(bob);

        Assert.Equal(TurnStep.Draw, game.State.CurrentStep);
        Assert.Equal(alice, game.State.Priority.Holder);
    }

    [Fact]
    public void All_four_players_must_pass_before_the_step_ends()
    {
        // The test the old model could not pass. Three passes out of four is not "all players
        // pass in succession", and with OpponentOf there was no fourth player to ask.
        var (game, seats) = TestCards.MultiPlayer(4);
        game.BeginPlay(withMulligans: false);
        var start = game.State.CurrentStep;

        game.PassPriority(seats[0]);
        game.PassPriority(seats[1]);
        game.PassPriority(seats[2]);
        Assert.Equal(start, game.State.CurrentStep);

        game.PassPriority(seats[3]);
        Assert.NotEqual(start, game.State.CurrentStep);
    }

    [Fact]
    public void Priority_passes_around_the_table_in_seating_order()
    {
        var (game, seats) = TestCards.MultiPlayer(4);
        game.BeginPlay(withMulligans: false);

        Assert.Equal(seats[0], game.State.Priority.Holder);
        game.PassPriority(seats[0]);
        Assert.Equal(seats[1], game.State.Priority.Holder);
        game.PassPriority(seats[1]);
        Assert.Equal(seats[2], game.State.Priority.Holder);
    }

    [Fact]
    public void Acting_breaks_the_run_of_passes()
    {
        // CR 117.4 says "in succession" — without anything in between. Three players passing,
        // then one casting a spell, means everyone must be given the chance again. An engine
        // counting passes rather than tracking them would resolve here.
        var (game, seats) = TestCards.MultiPlayer(4);
        game.BeginPlay(withMulligans: false);
        PassTo(game, seats, TurnStep.PrecombatMain);

        var instant = TestCards.PutInHand(game, seats[1], TestCards.Instant());

        game.PassPriority(seats[0]);
        game.CastSpell(seats[1], instant);

        Assert.Empty(game.State.Priority.Passed);
        Assert.Equal(seats[1], game.State.Priority.Holder);
        Assert.Single(game.State.Stack);
    }

    [Fact]
    public void The_top_of_the_stack_resolves_when_everyone_passes()
    {
        // CR 117.4. And CR 117.3b: the active player gets priority afterwards, not the caster.
        var (game, alice, bob) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);
        PassTo(game, [alice, bob], TurnStep.PrecombatMain);

        var creature = TestCards.PutInHand(game, alice, TestCards.Creature("Ox"));
        game.CastSpell(alice, creature);
        Assert.Single(game.State.Stack);

        game.PassPriority(alice);
        game.PassPriority(bob);

        Assert.Empty(game.State.Stack);
        Assert.Single(game.State.Battlefield);
        Assert.Equal(alice, game.State.Priority.Holder);
    }

    [Fact]
    public void The_stack_resolves_one_object_at_a_time()
    {
        // CR 608.1: only the top object resolves, and everyone gets priority again after it.
        var (game, alice, bob) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);
        PassTo(game, [alice, bob], TurnStep.PrecombatMain);

        var first = TestCards.PutInHand(game, alice, TestCards.Instant("Bolt"));
        var second = TestCards.PutInHand(game, alice, TestCards.Instant("Zap"));
        game.CastSpell(alice, first);
        game.CastSpell(alice, second);
        Assert.Equal(2, game.State.Stack.Count);

        game.PassPriority(alice);
        game.PassPriority(bob);

        Assert.Single(game.State.Stack);
        // Last on, first off (CR 405.2): Zap resolved, Bolt is still waiting.
        Assert.Equal("Bolt", game.State.GetObject(game.State.Stack[0]).Card.Name);
    }

    [Fact]
    public void A_spell_goes_on_top_of_the_stack()
    {
        var (game, alice, bob) = TestCards.TwoPlayer();
        game.BeginPlay(withMulligans: false);
        PassTo(game, [alice, bob], TurnStep.PrecombatMain);

        var first = TestCards.PutInHand(game, alice, TestCards.Instant("Bolt"));
        var second = TestCards.PutInHand(game, alice, TestCards.Instant("Zap"));
        game.CastSpell(alice, first);
        game.CastSpell(alice, second);

        Assert.Equal("Zap", game.State.GetObject(game.State.Stack[0]).Card.Name);
    }

    /// <summary>Passes priority around until the game reaches the wanted step.</summary>
    internal static void PassTo(Game game, IReadOnlyList<Guid> seats, TurnStep step) =>
        TestCards.PassToStep(game, step);
}
