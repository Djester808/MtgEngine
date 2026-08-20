using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// State-based actions (CR 704).
/// </summary>
/// <remarks>
/// The list is the easy part. The timing is what the previous engine got wrong twice over: it
/// ran these after every mutation rather than when a player would receive priority (CR 704.3),
/// and it never checked the empty-library loss at all — <c>HasLost</c> read
/// <c>Library.IsEmpty &amp;&amp; false</c>.
/// </remarks>
public sealed class StateBasedActionTests
{
    private static (Game Game, Guid Alice, Guid Bob) InMainPhase()
    {
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay();
        TestCards.PassToStep(game, TurnStep.PrecombatMain);
        return (game, alice, bob);
    }

    /// <summary>Gives everyone a chance to act, which is what makes CR 704.3 run.</summary>
    private static void Settle(Game game)
    {
        var holder = game.State.Priority.Holder;
        if (holder is not null)
            game.PassPriority(holder.Value);
    }

    [Fact]
    public void A_player_at_zero_life_loses()
    {
        // CR 704.5a.
        var (game, alice, _) = InMainPhase();

        game.ChangeLife(alice, -20);
        Assert.False(game.State.GetPlayer(alice).HasLost);

        Settle(game);

        Assert.True(game.State.GetPlayer(alice).HasLost);
        Assert.Contains(game.Log, e => e is PlayerLost { LosingRule: "704.5a" });
    }

    [Fact]
    public void Life_is_only_checked_when_someone_would_get_priority()
    {
        // CR 704.4. Dropping to zero and back up in between two checks is survivable, which is
        // the whole reason state-based actions are not run after every change.
        var (game, alice, _) = InMainPhase();

        game.ChangeLife(alice, -20);
        game.ChangeLife(alice, 5);
        Settle(game);

        Assert.False(game.State.GetPlayer(alice).HasLost);
        Assert.Equal(5, game.State.GetPlayer(alice).Life);
    }

    [Fact]
    public void Drawing_from_an_empty_library_loses_at_the_next_check()
    {
        // CR 704.5b, and the bug this replaces: the old engine wrote `Library.IsEmpty && false`
        // with a comment saying the real check was elsewhere. It was nowhere, and a player could
        // deck out and keep playing.
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 8);
        game.BeginPlay();
        TestCards.PassToStep(game, TurnStep.PrecombatMain);

        while (!game.State.GetPlayer(alice).Library.IsEmpty)
            game.Draw(alice);
        game.Draw(alice);

        Assert.False(game.State.GetPlayer(alice).HasLost);
        Settle(game);

        Assert.True(game.State.GetPlayer(alice).HasLost);
        Assert.Contains(game.Log, e => e is PlayerLost { LosingRule: "704.5b" });
        Assert.False(game.State.GetPlayer(bob).HasLost);
    }

    [Fact]
    public void Ten_poison_counters_lose_the_game()
    {
        // CR 704.5c.
        var (game, alice, _) = InMainPhase();
        var player = game.State.GetPlayer(alice);

        // No card grants poison yet, so the state is set directly; the rule under test is the
        // check, not the source of the counters.
        var poisoned = game.State.WithPlayer(player with { PoisonCounters = 10 });

        var actions = StateBasedActions.Check(poisoned, NoAbilities.Instance);

        Assert.Contains(actions, e => e is PlayerLost { LosingRule: "704.5c" });
    }

    [Fact]
    public void A_creature_with_zero_toughness_is_put_into_the_graveyard()
    {
        // CR 704.5f.
        var (game, alice, _) = InMainPhase();
        var creature = game.Create(alice, TestCards.Creature("Weakling", 1, 1), Zone.Battlefield);
        game.ChangeCounters(creature, CounterKinds.MinusOneMinusOne, 1);

        Settle(game);

        Assert.Empty(game.State.Battlefield);
        Assert.Single(game.State.GetPlayer(alice).Graveyard);
    }

    [Fact]
    public void Lethal_damage_destroys_a_creature()
    {
        // CR 704.5g.
        var (game, alice, _) = InMainPhase();
        var creature = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);

        game.MarkDamage(creature, 2);
        Assert.Single(game.State.Battlefield);

        Settle(game);

        Assert.Empty(game.State.Battlefield);
    }

    [Fact]
    public void Damage_below_toughness_does_nothing()
    {
        var (game, alice, _) = InMainPhase();
        var creature = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);

        game.MarkDamage(creature, 1);
        Settle(game);

        Assert.Single(game.State.Battlefield);
        Assert.Equal(1, game.State.GetObject(creature).Permanent!.DamageMarked);
    }

    [Fact]
    public void A_creature_that_grows_survives_damage_that_was_lethal()
    {
        // CR 704.5g compares damage with toughness at the check, not when the damage was dealt.
        var (game, alice, _) = InMainPhase();
        var creature = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);

        game.MarkDamage(creature, 2);
        game.ChangeCounters(creature, CounterKinds.PlusOnePlusOne, 1);
        Settle(game);

        Assert.Single(game.State.Battlefield);
    }

    [Fact]
    public void An_indestructible_creature_ignores_lethal_damage()
    {
        // CR 702.12b.
        var (game, alice, _) = InMainPhase();
        var creature = game.Create(alice, TestCards.Indestructible(), Zone.Battlefield);

        game.MarkDamage(creature, 10);
        Settle(game);

        Assert.Single(game.State.Battlefield);
    }

    [Fact]
    public void Both_creatures_die_when_each_has_lethal_damage()
    {
        // CR 704.3: performed simultaneously, as a single event. Neither dies first and survives.
        var (game, alice, bob) = InMainPhase();
        var mine = game.Create(alice, TestCards.Creature("Mine", 2, 2), Zone.Battlefield);
        var theirs = game.Create(bob, TestCards.Creature("Theirs", 2, 2), Zone.Battlefield);

        game.MarkDamage(mine, 2);
        game.MarkDamage(theirs, 2);
        Settle(game);

        Assert.Empty(game.State.Battlefield);
        Assert.Single(game.State.GetPlayer(alice).Graveyard);
        Assert.Single(game.State.GetPlayer(bob).Graveyard);
    }

    [Fact]
    public void Plus_and_minus_counters_annihilate()
    {
        // CR 704.5q.
        var (game, alice, _) = InMainPhase();
        var creature = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);

        game.ChangeCounters(creature, CounterKinds.PlusOnePlusOne, 3);
        game.ChangeCounters(creature, CounterKinds.MinusOneMinusOne, 1);
        Settle(game);

        var counters = game.State.GetObject(creature).Permanent!.Counters;
        Assert.Equal(2, counters[CounterKinds.PlusOnePlusOne]);
        Assert.False(counters.ContainsKey(CounterKinds.MinusOneMinusOne));
    }

    [Fact]
    public void The_legend_rule_keeps_only_one()
    {
        // CR 704.5j.
        var (game, alice, _) = InMainPhase();
        var first = game.Create(alice, TestCards.Legend("Karn"), Zone.Battlefield);
        var second = game.Create(alice, TestCards.Legend("Karn"), Zone.Battlefield);

        Settle(game);

        Assert.Single(game.State.Battlefield);
        // The older one stays: the rules let the controller choose, and until there is a way to
        // ask them, timestamp order is the only choice that is not arbitrary.
        Assert.Equal(first, game.State.Battlefield[0]);
        Assert.Equal(second.Value, ((ObjectMoved)game.Log.Last(e =>
            e is ObjectMoved { Cause: MoveCause.StateBasedAction })).OldId.Value);
    }

    [Fact]
    public void Two_different_legends_both_stay()
    {
        var (game, alice, _) = InMainPhase();
        game.Create(alice, TestCards.Legend("Karn"), Zone.Battlefield);
        game.Create(alice, TestCards.Legend("Urza"), Zone.Battlefield);

        Settle(game);

        Assert.Equal(2, game.State.Battlefield.Count);
    }

    [Fact]
    public void The_legend_rule_is_per_player()
    {
        // CR 704.5j: "controlled by the same player".
        var (game, alice, bob) = InMainPhase();
        game.Create(alice, TestCards.Legend("Karn"), Zone.Battlefield);
        game.Create(bob, TestCards.Legend("Karn"), Zone.Battlefield);

        Settle(game);

        Assert.Equal(2, game.State.Battlefield.Count);
    }

    [Fact]
    public void A_token_that_leaves_the_battlefield_ceases_to_exist()
    {
        // CR 704.5d.
        var (game, alice, _) = InMainPhase();
        var token = game.Create(alice, TestCards.Token(), Zone.Battlefield);

        var inGraveyard = game.Move(token, Zone.Graveyard, MoveCause.Destroy);
        Settle(game);

        Assert.False(game.State.TryGetObject(inGraveyard, out _));
        Assert.Empty(game.State.GetPlayer(alice).Graveyard);
    }

    [Fact]
    public void The_check_repeats_until_nothing_more_applies()
    {
        // CR 704.3: a token dies, goes to the graveyard, and the next check makes it cease to
        // exist. Two rounds, one settle.
        var (game, alice, _) = InMainPhase();
        var token = game.Create(alice, TestCards.Token(), Zone.Battlefield);
        game.MarkDamage(token, 5);

        Settle(game);

        Assert.Empty(game.State.Battlefield);
        Assert.Empty(game.State.GetPlayer(alice).Graveyard);
    }

    [Fact]
    public void A_settled_game_reports_nothing_to_do()
    {
        var (game, _, _) = InMainPhase();

        Assert.Empty(StateBasedActions.Check(game.State, NoAbilities.Instance));
    }
}
