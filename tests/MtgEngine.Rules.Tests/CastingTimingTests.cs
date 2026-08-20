using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// When a spell may be cast and a land may be played (CR 117.1, 305, 505.6).
/// </summary>
public sealed class CastingTimingTests
{
    private static (Game Game, Guid Alice, Guid Bob) InMainPhase()
    {
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay();
        PriorityTests.PassTo(game, [alice, bob], TurnStep.PrecombatMain);
        return (game, alice, bob);
    }

    [Fact]
    public void A_creature_can_be_cast_in_your_main_phase_with_an_empty_stack()
    {
        // CR 505.6a.
        var (game, alice, _) = InMainPhase();
        var creature = TestCards.PutInHand(game, alice, TestCards.Creature());

        var onStack = game.CastSpell(alice, creature);

        Assert.Equal(Zone.Stack, game.State.GetObject(onStack).Zone);
    }

    [Fact]
    public void A_creature_cannot_be_cast_during_the_upkeep()
    {
        // CR 117.1a: a noninstant spell needs a main phase.
        var (game, alice, _) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay();
        Assert.Equal(TurnStep.Upkeep, game.State.CurrentStep);
        var creature = TestCards.PutInHand(game, alice, TestCards.Creature());

        var ex = Assert.Throws<InvalidOperationException>(() => game.CastSpell(alice, creature));

        Assert.Contains("505.6a", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_creature_cannot_be_cast_with_something_on_the_stack()
    {
        // CR 117.1a: sorcery speed needs an empty stack, which is what makes a creature spell
        // impossible to cast in response to anything.
        var (game, alice, _) = InMainPhase();
        var first = TestCards.PutInHand(game, alice, TestCards.Creature("First"));
        var second = TestCards.PutInHand(game, alice, TestCards.Creature("Second"));

        game.CastSpell(alice, first);

        Assert.Throws<InvalidOperationException>(() => game.CastSpell(alice, second));
    }

    [Fact]
    public void An_instant_can_be_cast_in_response()
    {
        // CR 117.1a: an instant any time you have priority — including with a spell on the
        // stack, on someone else's turn, in a step that is not a main phase.
        var (game, alice, bob) = InMainPhase();
        var creature = TestCards.PutInHand(game, alice, TestCards.Creature());
        var instant = TestCards.PutInHand(game, bob, TestCards.Instant());

        game.CastSpell(alice, creature);
        game.PassPriority(alice);
        var response = game.CastSpell(bob, instant);

        Assert.Equal(2, game.State.Stack.Count);
        Assert.Equal(response, game.State.Stack[0]);
    }

    [Fact]
    public void An_instant_can_be_cast_on_another_players_turn()
    {
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay();
        var instant = TestCards.PutInHand(game, bob, TestCards.Instant());

        game.PassPriority(alice);
        game.CastSpell(bob, instant);

        Assert.Single(game.State.Stack);
    }

    [Fact]
    public void A_spell_cannot_be_cast_without_priority()
    {
        // CR 117.1.
        var (game, _, bob) = InMainPhase();
        var instant = TestCards.PutInHand(game, bob, TestCards.Instant());

        var ex = Assert.Throws<InvalidOperationException>(() => game.CastSpell(bob, instant));

        Assert.Contains("117.1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_land_is_played_not_cast()
    {
        // CR 305.1. It never uses the stack, so it cannot be responded to.
        var (game, alice, _) = InMainPhase();
        var land = TestCards.PutInHand(game, alice, TestCards.BasicLand());

        Assert.Throws<InvalidOperationException>(() => game.CastSpell(alice, land));

        var onBattlefield = game.PlayLand(alice, land);
        Assert.Empty(game.State.Stack);
        Assert.Equal(Zone.Battlefield, game.State.GetObject(onBattlefield).Zone);
    }

    [Fact]
    public void Only_one_land_per_turn()
    {
        // CR 505.6b.
        var (game, alice, _) = InMainPhase();
        game.PlayLand(alice, TestCards.PutInHand(game, alice, TestCards.BasicLand("Forest")));
        var second = TestCards.PutInHand(game, alice, TestCards.BasicLand("Island"));

        var ex = Assert.Throws<InvalidOperationException>(() => game.PlayLand(alice, second));

        Assert.Contains("505.6b", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, game.State.GetPlayer(alice).LandsPlayedThisTurn);
    }

    [Fact]
    public void The_land_drop_comes_back_next_turn()
    {
        var (game, alice, bob) = InMainPhase();
        game.PlayLand(alice, TestCards.PutInHand(game, alice, TestCards.BasicLand("Forest")));

        TestCards.PassToTurn(game, 3);
        PriorityTests.PassTo(game, [alice, bob], TurnStep.PrecombatMain);

        Assert.Equal(0, game.State.GetPlayer(alice).LandsPlayedThisTurn);
        game.PlayLand(alice, TestCards.PutInHand(game, alice, TestCards.BasicLand("Island")));
        Assert.Equal(2, game.State.Battlefield.Count);
    }

    [Fact]
    public void A_land_cannot_be_played_in_response_to_a_spell()
    {
        // CR 505.6b requires an empty stack, which is the same restriction as sorcery speed.
        var (game, alice, _) = InMainPhase();
        game.CastSpell(alice, TestCards.PutInHand(game, alice, TestCards.Creature()));
        var land = TestCards.PutInHand(game, alice, TestCards.BasicLand());

        Assert.Throws<InvalidOperationException>(() => game.PlayLand(alice, land));
    }

    [Fact]
    public void A_permanent_spell_becomes_a_permanent_when_it_resolves()
    {
        // CR 608.3.
        var (game, alice, bob) = InMainPhase();
        game.CastSpell(alice, TestCards.PutInHand(game, alice, TestCards.Creature("Ox")));

        game.PassPriority(alice);
        game.PassPriority(bob);

        var permanent = game.State.GetObject(game.State.Battlefield.Single());
        Assert.Equal("Ox", permanent.Card.Name);
        Assert.NotNull(permanent.Permanent);
        Assert.Equal(alice, permanent.ControllerId);
    }

    [Fact]
    public void An_instant_goes_to_its_owners_graveyard_when_it_resolves()
    {
        // CR 608.2m.
        var (game, alice, bob) = InMainPhase();
        game.CastSpell(alice, TestCards.PutInHand(game, alice, TestCards.Instant("Bolt")));

        game.PassPriority(alice);
        game.PassPriority(bob);

        Assert.Empty(game.State.Battlefield);
        Assert.Equal("Bolt", game.State.GetObject(game.State.GetPlayer(alice).Graveyard[0]).Card.Name);
    }
}
