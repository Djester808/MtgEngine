using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Combat, CR 506–511. Every creature here is made up: a power, a toughness and a set of
/// keywords the characteristics fake reports. Nothing in these tests names a card, and nothing
/// in the system under test could read one.
/// </summary>
public sealed class CombatTests
{
    [Fact]
    public void Two_creatures_of_equal_size_trade_damage()
    {
        var t = new Table();
        var attacker = t.Creature(t.A, 2, 2);
        var blocker = t.Creature(t.B, 2, 2);

        t.Attack(t.A, attacker, t.B);
        t.Block(t.B, new BlockDeclaration(blocker, attacker));
        t.Strike(firstStrike: false);

        // CR 510.2 — combat damage is dealt simultaneously, so both are dealt 2.
        Assert.Equal(2, t.DamageOn(blocker));
        Assert.Equal(2, t.DamageOn(attacker));
        Assert.Equal(20, t.Life(t.B));
    }

    [Fact]
    public void A_flyer_cannot_be_blocked_by_a_creature_without_flying_or_reach()
    {
        var t = new Table();
        var flyer = t.Creature(t.A, 2, 2, [KeywordKind.Flying]);
        var ground = t.Creature(t.B, 2, 2);
        t.Attack(t.A, flyer, t.B);

        var refused = Assert.Throws<RefusedException>(() => t.Block(t.B, new BlockDeclaration(ground, flyer)));

        Assert.Equal("702.9b", refused.Rule);
        Assert.Null(t.State.Combat!.Blockers.GetValueOrDefault(flyer));
    }

    [Fact]
    public void Reach_blocks_a_flyer()
    {
        var t = new Table();
        var flyer = t.Creature(t.A, 2, 2, [KeywordKind.Flying]);
        var reach = t.Creature(t.B, 1, 4, [KeywordKind.Reach]);

        t.Attack(t.A, flyer, t.B);
        t.Block(t.B, new BlockDeclaration(reach, flyer));
        t.Strike(firstStrike: false);

        Assert.Equal(2, t.DamageOn(reach));
        Assert.Equal(1, t.DamageOn(flyer));
    }

    [Fact]
    public void Trample_assigns_lethal_to_the_blocker_and_sends_the_excess_to_the_player()
    {
        var t = new Table();
        var attacker = t.Creature(t.A, 3, 3, [KeywordKind.Trample]);
        var blocker = t.Creature(t.B, 1, 1);

        t.Attack(t.A, attacker, t.B);
        t.Block(t.B, new BlockDeclaration(blocker, attacker));
        t.Strike(firstStrike: false);

        // CR 702.19b — lethal to the 1/1 is 1; the other 2 go to the player it was attacking.
        Assert.Equal(1, t.DamageOn(blocker));
        Assert.Equal(18, t.Life(t.B));
    }

    [Fact]
    public void Without_trample_all_the_damage_stays_on_the_blockers()
    {
        var t = new Table();
        var attacker = t.Creature(t.A, 3, 3);
        var blocker = t.Creature(t.B, 1, 1);

        t.Attack(t.A, attacker, t.B);
        t.Block(t.B, new BlockDeclaration(blocker, attacker));
        t.Strike(firstStrike: false);

        // CR 510.1c — a blocked creature assigns all its damage to the creatures blocking it.
        Assert.Equal(3, t.DamageOn(blocker));
        Assert.Equal(20, t.Life(t.B));
    }

    [Fact]
    public void First_strike_deals_its_damage_before_the_blocker_can_deal_any()
    {
        var t = new Table();
        var attacker = t.Creature(t.A, 2, 2, [KeywordKind.FirstStrike]);
        var blocker = t.Creature(t.B, 2, 2);

        t.Attack(t.A, attacker, t.B);
        t.Block(t.B, new BlockDeclaration(blocker, attacker));
        t.Strike(firstStrike: true);

        // CR 510.4 — only first and double strikers assign in the first combat damage step.
        Assert.Equal(2, t.DamageOn(blocker));
        Assert.Equal(0, t.DamageOn(attacker));

        // The blocker is destroyed by a state-based action between the steps, CR 704.5g.
        t.Remove(blocker);
        t.Strike(firstStrike: false);

        Assert.Equal(0, t.DamageOn(attacker));
    }

    [Fact]
    public void Double_strike_assigns_in_both_combat_damage_steps()
    {
        var t = new Table();
        var attacker = t.Creature(t.A, 2, 2, [KeywordKind.DoubleStrike]);
        var blocker = t.Creature(t.B, 3, 6);

        t.Attack(t.A, attacker, t.B);
        t.Block(t.B, new BlockDeclaration(blocker, attacker));
        t.Strike(firstStrike: true);

        Assert.Equal(2, t.DamageOn(blocker));
        Assert.Equal(0, t.DamageOn(attacker));

        t.Strike(firstStrike: false);

        // CR 702.4b — the double striker assigns again; the blocker assigns only now.
        Assert.Equal(4, t.DamageOn(blocker));
        Assert.Equal(3, t.DamageOn(attacker));
    }

    [Fact]
    public void Lifelink_gains_its_controller_the_damage_it_dealt()
    {
        var t = new Table();
        var attacker = t.Creature(t.A, 2, 2, [KeywordKind.Lifelink]);
        var blocker = t.Creature(t.B, 2, 2);

        t.Attack(t.A, attacker, t.B);
        t.Block(t.B, new BlockDeclaration(blocker, attacker));
        var events = t.Strike(firstStrike: false);

        // CR 702.15b — the life gain is in addition to the damage, and it is its own event.
        Assert.Equal(22, t.Life(t.A));
        Assert.Contains(events.OfType<LifeChanged>(), e => e.Player == t.A && e.Delta == 2);
        Assert.Equal(20, t.Life(t.B));
    }

    [Fact]
    public void Lifelink_on_an_unblocked_attacker_gains_what_the_player_lost()
    {
        var t = new Table();
        var attacker = t.Creature(t.A, 4, 4, [KeywordKind.Lifelink]);

        t.Attack(t.A, attacker, t.B);
        t.Strike(firstStrike: false);

        Assert.Equal(24, t.Life(t.A));
        Assert.Equal(16, t.Life(t.B));
    }

    [Fact]
    public void Vigilance_leaves_the_attacker_untapped()
    {
        var t = new Table();
        var vigilant = t.Creature(t.A, 2, 2, [KeywordKind.Vigilance]);
        var plain = t.Creature(t.A, 2, 2);

        t.Attack(
            t.A,
            new AttackDeclaration(vigilant, new CombatTarget(t.B, null)),
            new AttackDeclaration(plain, new CombatTarget(t.B, null)));

        // CR 702.20b — attacking does not cause creatures with vigilance to tap.
        Assert.False(t.Tapped(vigilant));
        Assert.True(t.Tapped(plain));
    }

    [Fact]
    public void A_summoning_sick_creature_cannot_attack()
    {
        var t = new Table();
        var sick = t.Creature(t.A, 2, 2, summoningSick: true);

        var refused = Assert.Throws<RefusedException>(() => t.Attack(t.A, sick, t.B));

        Assert.Equal("302.6", refused.Rule);
        Assert.Null(t.State.Combat);
    }

    [Fact]
    public void Haste_lets_a_summoning_sick_creature_attack()
    {
        var t = new Table();
        var hasty = t.Creature(t.A, 2, 2, [KeywordKind.Haste], summoningSick: true);

        t.Attack(t.A, hasty, t.B);

        Assert.True(t.State.Combat!.Attackers.ContainsKey(hasty));
        Assert.True(t.Tapped(hasty));
    }

    [Fact]
    public void A_tapped_creature_cannot_attack_or_block()
    {
        var t = new Table();
        var tappedAttacker = t.Creature(t.A, 2, 2, tapped: true);
        var attacker = t.Creature(t.A, 2, 2);
        var tappedBlocker = t.Creature(t.B, 2, 2, tapped: true);

        Assert.Equal("508.1a", Assert.Throws<RefusedException>(() => t.Attack(t.A, tappedAttacker, t.B)).Rule);

        t.Attack(t.A, attacker, t.B);

        Assert.Equal("509.1a", Assert.Throws<RefusedException>(() => t.Block(t.B, new BlockDeclaration(tappedBlocker, attacker))).Rule);
    }

    [Fact]
    public void A_creature_with_defender_cannot_attack()
    {
        var t = new Table();
        var wall = t.Creature(t.A, 0, 4, [KeywordKind.Defender]);

        // CR 702.3b.
        Assert.Equal("702.3b", Assert.Throws<RefusedException>(() => t.Attack(t.A, wall, t.B)).Rule);
    }

    [Fact]
    public void Menace_cannot_be_blocked_by_one_creature_but_can_by_two()
    {
        var t = new Table();
        var attacker = t.Creature(t.A, 3, 3, [KeywordKind.Menace]);
        var one = t.Creature(t.B, 1, 1);
        var two = t.Creature(t.B, 1, 1);
        t.Attack(t.A, attacker, t.B);

        var refused = Assert.Throws<RefusedException>(() => t.Block(t.B, new BlockDeclaration(one, attacker)));
        Assert.Equal("702.111b", refused.Rule);

        t.Block(t.B, new BlockDeclaration(one, attacker), new BlockDeclaration(two, attacker));

        Assert.Equal(2, t.State.Combat!.Blockers[attacker].Count);
    }

    [Fact]
    public void Deathtouch_makes_one_damage_lethal_when_assigning()
    {
        var t = new Table();
        var attacker = t.Creature(t.A, 2, 2, [KeywordKind.Deathtouch]);
        var one = t.Creature(t.B, 1, 3);
        var two = t.Creature(t.B, 1, 3);

        t.Attack(t.A, attacker, t.B);
        t.Block(t.B, new BlockDeclaration(one, attacker), new BlockDeclaration(two, attacker));
        t.Strike(firstStrike: false);

        // CR 702.2b — one damage from a deathtouch source is lethal, so 2 power covers both.
        Assert.Equal(1, t.DamageOn(one));
        Assert.Equal(1, t.DamageOn(two));
    }

    [Fact]
    public void Skulk_cannot_be_blocked_by_a_creature_with_greater_power()
    {
        var t = new Table();
        var sneak = t.Creature(t.A, 2, 2, [KeywordKind.Skulk]);
        var big = t.Creature(t.B, 3, 3);
        var small = t.Creature(t.B, 2, 2);
        t.Attack(t.A, sneak, t.B);

        Assert.Equal("702.118b", Assert.Throws<RefusedException>(() => t.Block(t.B, new BlockDeclaration(big, sneak))).Rule);

        t.Block(t.B, new BlockDeclaration(small, sneak));

        Assert.Equal(small, t.State.Combat!.Blockers[sneak].Single());
    }

    [Fact]
    public void Fear_cannot_be_blocked_except_by_black_or_artifact_creatures()
    {
        var t = new Table();
        var feared = t.Creature(t.A, 2, 2, [KeywordKind.Fear]);
        var white = t.Creature(t.B, 2, 2, colors: Color.White);
        var black = t.Creature(t.B, 2, 2, colors: Color.Black);
        t.Attack(t.A, feared, t.B);

        Assert.Equal("702.36b", Assert.Throws<RefusedException>(() => t.Block(t.B, new BlockDeclaration(white, feared))).Rule);

        t.Block(t.B, new BlockDeclaration(black, feared));

        Assert.Equal(black, t.State.Combat!.Blockers[feared].Single());
    }

    [Fact]
    public void Landwalk_cannot_be_blocked_while_the_defender_controls_that_land()
    {
        var t = new Table();
        var walker = t.Creature(t.A, 2, 2, statics: [new KeywordAbility(KeywordKind.Landwalk, Of: new ObjectFilter(Subtypes: ["Island"]))]);
        var blocker = t.Creature(t.B, 2, 2);
        var land = t.Creature(t.B, 0, 0, types: CardType.Land);
        t.Query.Match = (_, candidate) => candidate == land;
        t.Attack(t.A, walker, t.B);

        // CR 702.14b.
        Assert.Equal("702.14b", Assert.Throws<RefusedException>(() => t.Block(t.B, new BlockDeclaration(blocker, walker))).Rule);

        t.Query.Match = (_, _) => false;
        t.Block(t.B, new BlockDeclaration(blocker, walker));

        Assert.Equal(blocker, t.State.Combat!.Blockers[walker].Single());
    }

    [Fact]
    public void A_continuous_effect_saying_it_cannot_be_blocked_is_honoured()
    {
        var t = new Table();
        var unblockable = t.Creature(t.A, 2, 2, statics: [new StaticAbility(new CantBeBlocked())]);
        var blocker = t.Creature(t.B, 2, 2);
        t.Attack(t.A, unblockable, t.B);

        Assert.Equal("509.1b", Assert.Throws<RefusedException>(() => t.Block(t.B, new BlockDeclaration(blocker, unblockable))).Rule);
    }

    [Fact]
    public void A_continuous_effect_saying_a_creature_cannot_block_is_honoured()
    {
        var t = new Table();
        var attacker = t.Creature(t.A, 2, 2);
        var pacified = t.Creature(t.B, 2, 2, statics: [new StaticAbility(new CantBlock())]);
        t.Attack(t.A, attacker, t.B);

        Assert.Equal("509.1b", Assert.Throws<RefusedException>(() => t.Block(t.B, new BlockDeclaration(pacified, attacker))).Rule);
    }

    [Fact]
    public void A_creature_may_not_block_an_attacker_attacking_someone_else()
    {
        var t = new Table();
        var attacker = t.Creature(t.A, 2, 2);
        var bystander = t.Creature(t.A, 2, 2);
        t.Attack(t.A, attacker, t.B);

        // CR 509.1a — the attacker must be attacking that player or their permanents.
        Assert.Equal("509.1a", Assert.Throws<RefusedException>(() => t.Block(t.A, new BlockDeclaration(bystander, attacker))).Rule);
    }

    [Fact]
    public void An_unblocked_attacker_damages_the_player_and_a_commander_is_tracked()
    {
        var t = new Table();
        var general = t.Creature(t.A, 3, 3, commander: true);

        t.Attack(t.A, general, t.B);
        var events = t.Strike(firstStrike: false);

        // CR 510.1b and 903.10a.
        Assert.Equal(17, t.Life(t.B));
        Assert.Equal(3, t.State.Player(t.B).CommanderDamage[general]);
        Assert.Contains(events.OfType<DamageDealt>(), e => e.FromCommander && e.Combat && e.ToPlayer == t.B);
    }

    [Fact]
    public void A_blocked_attacker_whose_blocker_has_gone_assigns_no_damage()
    {
        var t = new Table();
        var attacker = t.Creature(t.A, 3, 3);
        var blocker = t.Creature(t.B, 1, 1);

        t.Attack(t.A, attacker, t.B);
        t.Block(t.B, new BlockDeclaration(blocker, attacker));
        t.Remove(blocker);
        t.Strike(firstStrike: false);

        // CR 510.1c — it stays blocked, and assigns nothing.
        Assert.Equal(20, t.Life(t.B));
    }

    [Fact]
    public void An_attacker_may_attack_a_planeswalker_an_opponent_controls()
    {
        var t = new Table();
        var attacker = t.Creature(t.A, 2, 2);
        var walker = t.Creature(t.B, 0, 0, types: CardType.Planeswalker);

        t.Attack(t.A, new AttackDeclaration(attacker, new CombatTarget(null, walker)));
        t.Strike(firstStrike: false);

        // CR 508.1c and 510.1b — the damage goes to the planeswalker, not the player.
        Assert.Equal(2, t.DamageOn(walker));
        Assert.Equal(20, t.Life(t.B));
    }

    [Fact]
    public void An_attacker_may_not_attack_its_own_controller_or_their_permanents()
    {
        var t = new Table();
        var attacker = t.Creature(t.A, 2, 2);
        var own = t.Creature(t.A, 0, 0, types: CardType.Planeswalker);

        Assert.Equal("506.2", Assert.Throws<RefusedException>(() => t.Attack(t.A, attacker, t.A)).Rule);
        Assert.Equal("506.2", Assert.Throws<RefusedException>(() => t.Attack(t.A, new AttackDeclaration(attacker, new CombatTarget(null, own)))).Rule);
    }

    [Fact]
    public void A_creature_that_must_attack_if_able_has_to_be_declared()
    {
        var t = new Table();
        var free = t.Creature(t.A, 2, 2);
        var goaded = t.Creature(t.A, 2, 2, statics: [new StaticAbility(new MustAttack())]);

        // CR 508.1d — a requirement left unobeyed makes the declaration illegal.
        Assert.Equal("508.1d", Assert.Throws<RefusedException>(() => t.Attack(t.A, free, t.B)).Rule);

        t.Attack(
            t.A,
            new AttackDeclaration(free, new CombatTarget(t.B, null)),
            new AttackDeclaration(goaded, new CombatTarget(t.B, null)));

        Assert.Equal(2, t.State.Combat!.Attackers.Count);
    }

    [Fact]
    public void Only_the_active_player_declares_attackers()
    {
        var t = new Table();
        var attacker = t.Creature(t.B, 2, 2);

        Assert.Equal("508.1a", Assert.Throws<RefusedException>(() => t.Attack(t.B, attacker, t.A)).Rule);
    }

    [Fact]
    public void End_of_combat_clears_the_combat_state_and_nothing_else()
    {
        var t = new Table();
        var attacker = t.Creature(t.A, 2, 2);
        t.Attack(t.A, attacker, t.B);
        var before = t.State;

        var events = t.End();

        // CR 511 — the step has no turn-based action; combat is simply over.
        Assert.Single(events);
        Assert.IsType<CombatEnded>(events[0]);
        Assert.Null(t.State.Combat);
        Assert.Equal(before.Objects, t.State.Objects);
        Assert.Equal(before.Players, t.State.Players);
    }

    [Fact]
    public void Declaring_blockers_before_attackers_is_refused()
    {
        var t = new Table();
        var blocker = t.Creature(t.B, 2, 2);

        Assert.Equal("509.1", Assert.Throws<RefusedException>(() => t.Block(t.B, new BlockDeclaration(blocker, new ObjectId(99)))).Rule);
    }
}
