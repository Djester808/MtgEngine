using FluentAssertions;
using MtgEngine.Domain.Enums;
using MtgEngine.Rules.Combat;
using Xunit;

namespace MtgEngine.Rules.Tests;

public class CombatEngineTests
{
    // =========================================================
    // Declare Attackers
    // =========================================================

    [Fact]
    public void Untapped_creature_without_summoning_sickness_can_attack()
    {
        var def = TestFactory.MakeCreatureDef(keywords: KeywordAbility.None);
        var attacker = TestFactory.MakePermanent(def, TestFactory.Player1Id, tapped: false, summoningSick: false);
        var state = TestFactory.MakeTwoPlayerGame(Phase.Combat, Step.DeclareAttackers)
            .WithPermanent(attacker);

        var result = CombatEngine.DeclareAttackers(state, TestFactory.Player1Id, [attacker.PermanentId]);

        result.Combat!.IsAttacking(attacker.PermanentId).Should().BeTrue();
        result.Battlefield.First(p => p.PermanentId == attacker.PermanentId).IsTapped.Should().BeTrue();
    }

    [Fact]
    public void Tapped_creature_cannot_attack()
    {
        var def = TestFactory.MakeCreatureDef();
        var attacker = TestFactory.MakePermanent(def, TestFactory.Player1Id, tapped: true, summoningSick: false);
        var state = TestFactory.MakeTwoPlayerGame(Phase.Combat, Step.DeclareAttackers)
            .WithPermanent(attacker);

        var act = () => CombatEngine.DeclareAttackers(state, TestFactory.Player1Id, [attacker.PermanentId]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Summoning_sick_creature_cannot_attack()
    {
        var def = TestFactory.MakeCreatureDef();
        var attacker = TestFactory.MakePermanent(def, TestFactory.Player1Id, tapped: false, summoningSick: true);
        var state = TestFactory.MakeTwoPlayerGame(Phase.Combat, Step.DeclareAttackers)
            .WithPermanent(attacker);

        var act = () => CombatEngine.DeclareAttackers(state, TestFactory.Player1Id, [attacker.PermanentId]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Haste_creature_can_attack_with_summoning_sickness()
    {
        var def = TestFactory.MakeCreatureDef(keywords: KeywordAbility.Haste);
        var attacker = TestFactory.MakePermanent(def, TestFactory.Player1Id, tapped: false, summoningSick: true);
        var state = TestFactory.MakeTwoPlayerGame(Phase.Combat, Step.DeclareAttackers)
            .WithPermanent(attacker);

        var result = CombatEngine.DeclareAttackers(state, TestFactory.Player1Id, [attacker.PermanentId]);

        result.Combat!.IsAttacking(attacker.PermanentId).Should().BeTrue();
    }

    [Fact]
    public void Vigilance_attacker_does_not_tap()
    {
        var def = TestFactory.MakeCreatureDef(keywords: KeywordAbility.Vigilance);
        var attacker = TestFactory.MakePermanent(def, TestFactory.Player1Id, tapped: false, summoningSick: false);
        var state = TestFactory.MakeTwoPlayerGame(Phase.Combat, Step.DeclareAttackers)
            .WithPermanent(attacker);

        var result = CombatEngine.DeclareAttackers(state, TestFactory.Player1Id, [attacker.PermanentId]);

        result.Battlefield.First(p => p.PermanentId == attacker.PermanentId).IsTapped.Should().BeFalse();
    }

    // =========================================================
    // Declare Blockers
    // =========================================================

    [Fact]
    public void Ground_creature_can_block_ground_attacker()
    {
        var attackerDef = TestFactory.MakeCreatureDef("Attacker");
        var blockerDef = TestFactory.MakeCreatureDef("Blocker");

        var attacker = TestFactory.MakePermanent(attackerDef, TestFactory.Player1Id, summoningSick: false);
        var blocker = TestFactory.MakePermanent(blockerDef, TestFactory.Player2Id);

        var state = TestFactory.MakeTwoPlayerGame(Phase.Combat, Step.DeclareBlockers)
            .WithPermanent(attacker)
            .WithPermanent(blocker) with
        {
            Combat = new() { AttackersToBlockers = System.Collections.Immutable.ImmutableDictionary.CreateRange(
                new[] { new System.Collections.Generic.KeyValuePair<Guid, System.Collections.Immutable.ImmutableList<Guid>>(attacker.PermanentId, System.Collections.Immutable.ImmutableList<Guid>.Empty) }),
                AttackersDeclared = true }
        };

        var result = CombatEngine.DeclareBlockers(state, TestFactory.Player2Id,
            new Dictionary<Guid, Guid> { [blocker.PermanentId] = attacker.PermanentId });

        result.Combat!.GetBlockers(attacker.PermanentId).Should().Contain(blocker.PermanentId);
    }

    [Fact]
    public void Non_flying_creature_cannot_block_flying_attacker()
    {
        var attackerDef = TestFactory.MakeCreatureDef("Flyer", keywords: KeywordAbility.Flying);
        var blockerDef = TestFactory.MakeCreatureDef("Ground");

        var attacker = TestFactory.MakePermanent(attackerDef, TestFactory.Player1Id, summoningSick: false);
        var blocker = TestFactory.MakePermanent(blockerDef, TestFactory.Player2Id);

        var state = TestFactory.MakeTwoPlayerGame(Phase.Combat, Step.DeclareBlockers)
            .WithPermanent(attacker)
            .WithPermanent(blocker) with
        {
            Combat = new() { AttackersToBlockers = System.Collections.Immutable.ImmutableDictionary.CreateRange(
                new[] { new System.Collections.Generic.KeyValuePair<Guid, System.Collections.Immutable.ImmutableList<Guid>>(attacker.PermanentId, System.Collections.Immutable.ImmutableList<Guid>.Empty) }),
                AttackersDeclared = true }
        };

        var act = () => CombatEngine.DeclareBlockers(state, TestFactory.Player2Id,
            new Dictionary<Guid, Guid> { [blocker.PermanentId] = attacker.PermanentId });

        act.Should().Throw<InvalidOperationException>().WithMessage("*flying*");
    }

    [Fact]
    public void Reach_creature_can_block_flying_attacker()
    {
        var attackerDef = TestFactory.MakeCreatureDef("Flyer", keywords: KeywordAbility.Flying);
        var blockerDef = TestFactory.MakeCreatureDef("Reach creature", keywords: KeywordAbility.Reach);

        var attacker = TestFactory.MakePermanent(attackerDef, TestFactory.Player1Id, summoningSick: false);
        var blocker = TestFactory.MakePermanent(blockerDef, TestFactory.Player2Id);

        var state = TestFactory.MakeTwoPlayerGame(Phase.Combat, Step.DeclareBlockers)
            .WithPermanent(attacker)
            .WithPermanent(blocker) with
        {
            Combat = new() { AttackersToBlockers = System.Collections.Immutable.ImmutableDictionary.CreateRange(
                new[] { new System.Collections.Generic.KeyValuePair<Guid, System.Collections.Immutable.ImmutableList<Guid>>(attacker.PermanentId, System.Collections.Immutable.ImmutableList<Guid>.Empty) }),
                AttackersDeclared = true }
        };

        var result = CombatEngine.DeclareBlockers(state, TestFactory.Player2Id,
            new Dictionary<Guid, Guid> { [blocker.PermanentId] = attacker.PermanentId });

        result.Combat!.GetBlockers(attacker.PermanentId).Should().Contain(blocker.PermanentId);
    }

    // =========================================================
    // Combat damage
    // =========================================================

    [Fact]
    public void Unblocked_attacker_deals_damage_to_defending_player()
    {
        var def = TestFactory.MakeCreatureDef(power: 3, toughness: 3);
        var attacker = TestFactory.MakePermanent(def, TestFactory.Player1Id, summoningSick: false);

        var state = TestFactory.MakeTwoPlayerGame(Phase.Combat, Step.CombatDamage)
            .WithPermanent(attacker) with
        {
            Combat = new()
            {
                AttackersToBlockers = System.Collections.Immutable.ImmutableDictionary.CreateRange(
                    new[] { new System.Collections.Generic.KeyValuePair<Guid, System.Collections.Immutable.ImmutableList<Guid>>(attacker.PermanentId, System.Collections.Immutable.ImmutableList<Guid>.Empty) }),
                AttackersDeclared = true,
                BlockersDeclared = true,
            }
        };

        var result = CombatEngine.AssignCombatDamage(state, firstStrike: false);

        result.GetPlayer(TestFactory.Player2Id).Life.Should().Be(17); // 20 - 3
    }

    [Fact]
    public void Trample_attacker_deals_overflow_damage_to_player()
    {
        var attackerDef = TestFactory.MakeCreatureDef("Trampler", power: 5, toughness: 5, keywords: KeywordAbility.Trample);
        var blockerDef = TestFactory.MakeCreatureDef("Blocker", power: 2, toughness: 2);

        var attacker = TestFactory.MakePermanent(attackerDef, TestFactory.Player1Id, summoningSick: false);
        var blocker = TestFactory.MakePermanent(blockerDef, TestFactory.Player2Id);

        var state = TestFactory.MakeTwoPlayerGame(Phase.Combat, Step.CombatDamage)
            .WithPermanent(attacker)
            .WithPermanent(blocker) with
        {
            Combat = new()
            {
                AttackersToBlockers = System.Collections.Immutable.ImmutableDictionary.CreateRange(
                    new[] { new System.Collections.Generic.KeyValuePair<Guid, System.Collections.Immutable.ImmutableList<Guid>>(
                        attacker.PermanentId, System.Collections.Immutable.ImmutableList.Create(blocker.PermanentId)) }),
                AttackersDeclared = true,
                BlockersDeclared = true,
            }
        };

        var result = CombatEngine.AssignCombatDamage(state, firstStrike: false);

        // Attacker deals 2 to blocker (lethal), 3 trample to player
        result.GetPlayer(TestFactory.Player2Id).Life.Should().Be(17); // 20 - 3
        result.GetPermanent(blocker.PermanentId).DamageMarked.Should().Be(2);
    }

    [Fact]
    public void Lifelink_attacker_gains_life_for_controller()
    {
        var def = TestFactory.MakeCreatureDef(power: 3, toughness: 3, keywords: KeywordAbility.Lifelink);
        var attacker = TestFactory.MakePermanent(def, TestFactory.Player1Id, summoningSick: false);

        var state = TestFactory.MakeTwoPlayerGame(Phase.Combat, Step.CombatDamage)
            .WithPermanent(attacker) with
        {
            Combat = new()
            {
                AttackersToBlockers = System.Collections.Immutable.ImmutableDictionary.CreateRange(
                    new[] { new System.Collections.Generic.KeyValuePair<Guid, System.Collections.Immutable.ImmutableList<Guid>>(attacker.PermanentId, System.Collections.Immutable.ImmutableList<Guid>.Empty) }),
                AttackersDeclared = true,
                BlockersDeclared = true,
            }
        };

        var result = CombatEngine.AssignCombatDamage(state, firstStrike: false);

        result.GetPlayer(TestFactory.Player1Id).Life.Should().Be(23); // 20 + 3
    }
}
