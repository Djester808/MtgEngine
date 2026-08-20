using MtgEngine.Domain.Enums;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Combat (CR 506–511).
/// </summary>
/// <remarks>
/// Every characteristic combat reads goes through the layer system, so these are also tests that
/// a creature's <em>current</em> power, toughness and keywords are what matters — not what was
/// printed on it.
/// </remarks>
public sealed class CombatTests
{
    /// <summary>Turn 1's precombat main phase, where a test places what it needs.</summary>
    private static (Game Game, Guid Alice, Guid Bob) BeforeCombat()
    {
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay(withMulligans: false);
        TestCards.PassToStep(game, TurnStep.PrecombatMain);
        return (game, alice, bob);
    }

    /// <summary>
    /// Plays on to Alice's next declare attackers step, by which time everything placed above
    /// has been through an untap step.
    /// </summary>
    /// <remarks>
    /// Summoning sickness wears off when its controller's turn begins (CR 302.6), so a test that
    /// wants an attacker has to let a turn pass rather than reach into the permanent and clear
    /// the flag. A backdoor would let a genuinely sick creature attack in a test and never in a
    /// game, which is the sort of difference that makes a suite stop meaning anything.
    /// </remarks>
    private static void Ready(Game game)
    {
        TestCards.PassToTurn(game, 3);
        TestCards.PassToStep(game, TurnStep.DeclareAttackers);
    }

    /// <summary>Turn 1's declare attackers step, for testing a creature that just arrived.</summary>
    private static (Game Game, Guid Alice, Guid Bob) AtDeclareAttackers()
    {
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay(withMulligans: false);
        TestCards.PassToStep(game, TurnStep.DeclareAttackers);
        return (game, alice, bob);
    }

    [Fact]
    public void A_creature_attacks_and_taps()
    {
        // CR 508.1f: attacking taps the creature, and that is not a cost.
        var (game, alice, bob) = BeforeCombat();
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [bear] = bob });

        Assert.True(game.State.GetObject(bear).Permanent!.IsTapped);
        Assert.Single(game.State.Combat.Attackers);
    }

    [Fact]
    public void A_creature_with_vigilance_does_not_tap_to_attack()
    {
        // CR 702.20b.
        var (game, alice, bob) = BeforeCombat();
        var knight = game.Create(alice, TestCards.WithKeyword("Knight", KeywordAbility.Vigilance), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [knight] = bob });

        Assert.False(game.State.GetObject(knight).Permanent!.IsTapped);
    }

    [Fact]
    public void A_summoning_sick_creature_cannot_attack()
    {
        // CR 302.6, 508.1a.
        var (game, alice, bob) = AtDeclareAttackers();
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [bear] = bob }));

        Assert.Contains("302.6", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Haste_lets_a_new_creature_attack()
    {
        // CR 702.10b.
        var (game, alice, bob) = AtDeclareAttackers();
        var hasty = game.Create(alice, TestCards.WithKeyword("Raider", KeywordAbility.Haste), Zone.Battlefield);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [hasty] = bob });

        Assert.Single(game.State.Combat.Attackers);
    }

    [Fact]
    public void Defender_cannot_attack()
    {
        // CR 702.3b.
        var (game, alice, bob) = BeforeCombat();
        var wall = game.Create(alice, TestCards.WithKeyword("Wall", KeywordAbility.Defender), Zone.Battlefield);
        Ready(game);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [wall] = bob }));

        Assert.Contains("702.3b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unblocked_creature_damages_the_player()
    {
        var (game, alice, bob) = BeforeCombat();
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [bear] = bob });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.EndOfCombat);

        Assert.Equal(18, game.State.GetPlayer(bob).Life);
    }

    [Fact]
    public void A_blocked_creature_damages_its_blocker_instead()
    {
        var (game, alice, bob) = BeforeCombat();
        var attacker = game.Create(alice, TestCards.Creature("Attacker", 2, 2), Zone.Battlefield);
        var blocker = game.Create(bob, TestCards.Creature("Blocker", 1, 3), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [attacker] = bob });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.DeclareBlockers);
        game.DeclareBlockers(bob, new Dictionary<ObjectId, IReadOnlyList<ObjectId>>
        {
            [attacker] = [blocker],
        });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.EndOfCombat);

        Assert.Equal(20, game.State.GetPlayer(bob).Life);
        Assert.Equal(2, game.State.GetObject(blocker).Permanent!.DamageMarked);
        Assert.Equal(1, game.State.GetObject(attacker).Permanent!.DamageMarked);
    }

    [Fact]
    public void Both_creatures_die_when_each_deals_lethal()
    {
        // CR 510.2: combat damage is dealt simultaneously, so neither is destroyed before the
        // other assigns.
        var (game, alice, bob) = BeforeCombat();
        var attacker = game.Create(alice, TestCards.Creature("Attacker", 2, 2), Zone.Battlefield);
        var blocker = game.Create(bob, TestCards.Creature("Blocker", 2, 2), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [attacker] = bob });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.DeclareBlockers);
        game.DeclareBlockers(bob, new Dictionary<ObjectId, IReadOnlyList<ObjectId>>
        {
            [attacker] = [blocker],
        });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.EndOfCombat);

        Assert.Empty(game.State.Battlefield);
    }

    [Fact]
    public void A_blocked_creature_deals_nothing_to_the_player_even_if_the_blocker_dies()
    {
        // CR 509.1h: it remains blocked even when every creature blocking it has gone.
        var (game, alice, bob) = BeforeCombat();
        var attacker = game.Create(alice, TestCards.Creature("Attacker", 5, 5), Zone.Battlefield);
        var chump = game.Create(bob, TestCards.Creature("Chump", 1, 1), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [attacker] = bob });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.DeclareBlockers);
        game.DeclareBlockers(bob, new Dictionary<ObjectId, IReadOnlyList<ObjectId>>
        {
            [attacker] = [chump],
        });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.EndOfCombat);

        Assert.Equal(20, game.State.GetPlayer(bob).Life);
    }

    [Fact]
    public void Trample_sends_the_excess_to_the_player()
    {
        // CR 702.19b: lethal damage to each blocker, and the rest to the player.
        var (game, alice, bob) = BeforeCombat();
        var attacker = game.Create(alice, TestCards.WithKeyword("Beast", KeywordAbility.Trample, 5, 5), Zone.Battlefield);
        var chump = game.Create(bob, TestCards.Creature("Chump", 1, 1), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [attacker] = bob });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.DeclareBlockers);
        game.DeclareBlockers(bob, new Dictionary<ObjectId, IReadOnlyList<ObjectId>>
        {
            [attacker] = [chump],
        });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.EndOfCombat);

        Assert.Equal(16, game.State.GetPlayer(bob).Life);
    }

    [Fact]
    public void Deathtouch_makes_one_damage_lethal_for_assignment()
    {
        // CR 702.2b with CR 510.1c: one damage is lethal, so a deathtouch trampler only has to
        // assign one to each blocker before the rest goes through.
        var (game, alice, bob) = BeforeCombat();
        var attacker = game.Create(
            alice, TestCards.WithKeyword("Wurm", KeywordAbility.Deathtouch | KeywordAbility.Trample, 5, 5), Zone.Battlefield);
        var wall = game.Create(bob, TestCards.Creature("Wall", 0, 4), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [attacker] = bob });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.DeclareBlockers);
        game.DeclareBlockers(bob, new Dictionary<ObjectId, IReadOnlyList<ObjectId>>
        {
            [attacker] = [wall],
        });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.EndOfCombat);

        Assert.Equal(16, game.State.GetPlayer(bob).Life);
        Assert.DoesNotContain(game.State.Battlefield, id => game.State.GetObject(id).Card.Name == "Wall");
    }

    [Fact]
    public void Damage_is_assigned_to_multiple_blockers_in_order()
    {
        // CR 510.1c: lethal to the first before any goes to the second.
        var (game, alice, bob) = BeforeCombat();
        var attacker = game.Create(alice, TestCards.Creature("Attacker", 4, 4), Zone.Battlefield);
        var first = game.Create(bob, TestCards.Creature("First", 1, 3), Zone.Battlefield);
        var second = game.Create(bob, TestCards.Creature("Second", 1, 3), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [attacker] = bob });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.DeclareBlockers);
        game.DeclareBlockers(bob, new Dictionary<ObjectId, IReadOnlyList<ObjectId>>
        {
            [attacker] = [first, second],
        });

        // CR 510.1c: the attacking player divides, and is asked. The engine used to take the
        // order the blocks were declared in — the defending player's order, which is precisely
        // the player who should not get to decide which of their creatures dies.
        TestCards.PassUntil(game, () => game.State.Choice is not null);
        var choice = game.State.Choice!;
        Assert.Equal(ChoiceKind.DivideCombatDamage, choice.Kind);
        Assert.Equal(alice, choice.PlayerId);

        // Assign to the second blocker first, which the declaration order would never produce.
        game.Choose(alice, [second.Value.ToString("N"), first.Value.ToString("N")]);
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.EndOfCombat);

        Assert.False(game.State.TryGetObject(second, out _));
        Assert.Equal(1, game.State.GetObject(first).Permanent!.DamageMarked);
    }

    [Fact]
    public void Flying_cannot_be_blocked_by_a_creature_without_flying_or_reach()
    {
        // CR 702.9b.
        var (game, alice, bob) = BeforeCombat();
        var flyer = game.Create(alice, TestCards.WithKeyword("Drake", KeywordAbility.Flying), Zone.Battlefield);
        var ground = game.Create(bob, TestCards.Creature("Ground", 2, 2), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [flyer] = bob });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.DeclareBlockers);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            game.DeclareBlockers(bob, new Dictionary<ObjectId, IReadOnlyList<ObjectId>>
            {
                [flyer] = [ground],
            }));

        Assert.Contains("702.9b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reach_can_block_a_flyer()
    {
        var (game, alice, bob) = BeforeCombat();
        var flyer = game.Create(alice, TestCards.WithKeyword("Drake", KeywordAbility.Flying), Zone.Battlefield);
        var spider = game.Create(bob, TestCards.WithKeyword("Spider", KeywordAbility.Reach, 1, 4), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [flyer] = bob });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.DeclareBlockers);
        game.DeclareBlockers(bob, new Dictionary<ObjectId, IReadOnlyList<ObjectId>>
        {
            [flyer] = [spider],
        });

        Assert.Single(game.State.Combat.Blocked);
    }

    [Fact]
    public void Menace_cannot_be_blocked_by_exactly_one_creature()
    {
        // CR 702.111b.
        var (game, alice, bob) = BeforeCombat();
        var brute = game.Create(alice, TestCards.WithKeyword("Brute", KeywordAbility.Menace, 3, 3), Zone.Battlefield);
        var one = game.Create(bob, TestCards.Creature("One", 2, 2), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [brute] = bob });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.DeclareBlockers);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            game.DeclareBlockers(bob, new Dictionary<ObjectId, IReadOnlyList<ObjectId>>
            {
                [brute] = [one],
            }));

        Assert.Contains("702.111b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void First_strike_kills_before_the_other_creature_can_answer()
    {
        // CR 510.4: two combat damage steps, and the ordinary creature is already dead by the
        // second, so it never assigns.
        var (game, alice, bob) = BeforeCombat();
        var striker = game.Create(alice, TestCards.WithKeyword("Knight", KeywordAbility.FirstStrike, 2, 2), Zone.Battlefield);
        var ordinary = game.Create(bob, TestCards.Creature("Ordinary", 2, 2), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [striker] = bob });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.DeclareBlockers);
        game.DeclareBlockers(bob, new Dictionary<ObjectId, IReadOnlyList<ObjectId>>
        {
            [striker] = [ordinary],
        });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.EndOfCombat);

        Assert.False(game.State.TryGetObject(ordinary, out _));
        Assert.Equal(0, game.State.GetObject(striker).Permanent!.DamageMarked);
    }

    [Fact]
    public void Double_strike_deals_damage_in_both_steps()
    {
        // CR 702.4b.
        var (game, alice, bob) = BeforeCombat();
        var hero = game.Create(alice, TestCards.WithKeyword("Hero", KeywordAbility.DoubleStrike, 2, 2), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [hero] = bob });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.EndOfCombat);

        Assert.Equal(16, game.State.GetPlayer(bob).Life);
    }

    [Fact]
    public void A_lords_bonus_counts_in_combat()
    {
        // Combat asks for the computed power, so a pumped creature hits for the pumped amount.
        var (game, alice, bob) = BeforeCombat();
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);
        Ready(game);
        game.ChangeCounters(bear, CounterKinds.PlusOnePlusOne, 2);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [bear] = bob });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.EndOfCombat);

        Assert.Equal(16, game.State.GetPlayer(bob).Life);
    }

    [Fact]
    public void Combat_ends_and_clears_when_the_phase_does()
    {
        // CR 511.3.
        var (game, alice, bob) = BeforeCombat();
        var bear = game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [bear] = bob });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.PostcombatMain);

        Assert.Empty(game.State.Combat.Attackers);
        Assert.False(game.State.Combat.AttackersDeclared);
        Assert.Contains(game.Log, e => e is CombatEnded);
    }

    [Fact]
    public void Losing_all_your_life_to_combat_loses_the_game()
    {
        var (game, alice, bob) = BeforeCombat();
        var giant = game.Create(alice, TestCards.Creature("Giant", 20, 20), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [giant] = bob });
        TestCards.PassUntil(game, () => game.State.IsOver);

        Assert.True(game.State.GetPlayer(bob).HasLost);
        Assert.Equal(alice, game.State.WinnerId);
    }

    [Fact]
    public void A_game_with_combat_still_replays()
    {
        var (game, alice, bob) = BeforeCombat();
        var attacker = game.Create(alice, TestCards.Creature("Attacker", 2, 2), Zone.Battlefield);
        var blocker = game.Create(bob, TestCards.Creature("Blocker", 1, 3), Zone.Battlefield);
        Ready(game);

        game.DeclareAttackers(alice, new Dictionary<ObjectId, Guid> { [attacker] = bob });
        TestCards.PassUntil(game, () => game.State.CurrentStep == TurnStep.DeclareBlockers);
        game.DeclareBlockers(bob, new Dictionary<ObjectId, IReadOnlyList<ObjectId>>
        {
            [attacker] = [blocker],
        });
        TestCards.PassToTurn(game, 2);

        Assert.Equal(game.State, GameReducer.Replay(game.Log));
    }
}
