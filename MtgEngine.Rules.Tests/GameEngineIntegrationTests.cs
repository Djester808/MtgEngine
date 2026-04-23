using FluentAssertions;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Domain.ValueObjects;
using System.Collections.Immutable;
using Xunit;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Integration tests that exercise the full game engine through GameEngine.
/// These tests simulate real game scenarios from start to finish.
/// </summary>
public class GameEngineIntegrationTests
{
    // =========================================================
    // Helpers
    // =========================================================

    private static GameState MakeGameInMainPhase()
    {
        var state = TestFactory.MakeTwoPlayerGame(Phase.PreCombatMain, Step.Main) with
        {
            IsFirstTurn = false,
        };
        // Reset land play flag
        var p1 = state.GetPlayer(TestFactory.Player1Id) with { HasLandPlayedThisTurn = false };
        return state.UpdatePlayer(p1);
    }

    // =========================================================
    // Scenario: Play a land and tap it for mana, then cast a creature
    // =========================================================

    [Fact]
    public void Can_play_land_tap_it_and_cast_a_creature()
    {
        // Setup: Player1 has a Forest and a Grizzly Bears (2/2 for 1G) in hand
        var forestDef = TestFactory.MakeLandDef("Forest");
        var bearsDef = TestFactory.MakeCreatureDef("Grizzly Bears", 2, 2, "1G");

        var forest = TestFactory.MakeCard(forestDef, TestFactory.Player1Id);
        var bears = TestFactory.MakeCard(bearsDef, TestFactory.Player1Id);

        var state = MakeGameInMainPhase()
            .WithCardInHand(TestFactory.Player1Id, forest)
            .WithCardInHand(TestFactory.Player1Id, bears)
            .WithMana(TestFactory.Player1Id, ManaColor.Green, 1); // 1 green already in pool

        // Step 1: Play the Forest
        state = GameEngine.PlayLand(state, TestFactory.Player1Id, forest.CardId);
        state.GetPlayer(TestFactory.Player1Id).Hand.Should().HaveCount(1); // bears still in hand
        state.Battlefield.Should().HaveCount(1);

        // Step 2: Tap the Forest for mana
        var forestPermanent = state.Battlefield.First();
        state = GameEngine.ActivateMana(state, TestFactory.Player1Id, forestPermanent.PermanentId);
        state.GetPlayer(TestFactory.Player1Id).ManaPool.Amounts[ManaColor.Green].Should().Be(2); // 1 existing + 1 from forest

        // Step 3: Cast Grizzly Bears (1G)
        state = GameEngine.CastSpell(state, TestFactory.Player1Id, bears.CardId);
        state.Stack.IsEmpty.Should().BeFalse();
        state.GetPlayer(TestFactory.Player1Id).Hand.Should().BeEmpty();
        state.GetPlayer(TestFactory.Player1Id).ManaPool.Total.Should().Be(0);

        // Step 4: Opponent passes, then active player resolves
        state = GameEngine.PassPriority(state, TestFactory.Player1Id); // AP passes to opp
        state = GameEngine.PassPriority(state, TestFactory.Player2Id); // Opp passes, spell resolves

        state.Stack.IsEmpty.Should().BeTrue();
        state.Battlefield.Should().HaveCount(2); // forest + bears
        state.Battlefield.Any(p => p.Name == "Grizzly Bears").Should().BeTrue();
    }

    // =========================================================
    // Scenario: Combat - attacker kills blocker
    // =========================================================

    [Fact]
    public void Attacker_and_blocker_kill_each_other_in_combat()
    {
        var def2x2 = TestFactory.MakeCreatureDef("Bear", 2, 2);

        var attacker = TestFactory.MakePermanent(def2x2, TestFactory.Player1Id, summoningSick: false);
        var blocker = TestFactory.MakePermanent(def2x2, TestFactory.Player2Id);

        var state = TestFactory.MakeTwoPlayerGame(Phase.Combat, Step.DeclareAttackers)
            .WithPermanent(attacker)
            .WithPermanent(blocker);

        // Declare attacker
        state = GameEngine.DeclareAttackers(state, TestFactory.Player1Id, [attacker.PermanentId]);

        // Advance to declare blockers
        state = state with { CurrentStep = Step.DeclareBlockers };

        // Declare blocker
        state = GameEngine.DeclareBlockers(state, TestFactory.Player2Id,
            new Dictionary<Guid, Guid> { [blocker.PermanentId] = attacker.PermanentId });

        // Advance to combat damage
        state = state with { CurrentStep = Step.CombatDamage };

        // Apply damage
        state = GameEngine.ApplyCombatDamage(state);

        // Both should be dead (SBAs ran)
        state.Battlefield.Should().BeEmpty();
        state.GetPlayer(TestFactory.Player1Id).Graveyard.Should().HaveCount(1);
        state.GetPlayer(TestFactory.Player2Id).Graveyard.Should().HaveCount(1);
    }

    // =========================================================
    // Scenario: Unblocked attacker reduces player life
    // =========================================================

    [Fact]
    public void Unblocked_attacker_deals_damage_to_player()
    {
        var def = TestFactory.MakeCreatureDef("Attacker", 3, 3);
        var attacker = TestFactory.MakePermanent(def, TestFactory.Player1Id, summoningSick: false);

        var state = TestFactory.MakeTwoPlayerGame(Phase.Combat, Step.DeclareAttackers)
            .WithPermanent(attacker);

        state = GameEngine.DeclareAttackers(state, TestFactory.Player1Id, [attacker.PermanentId]);
        state = state with { CurrentStep = Step.DeclareBlockers };
        state = GameEngine.DeclareBlockers(state, TestFactory.Player2Id, new Dictionary<Guid, Guid>());
        state = state with { CurrentStep = Step.CombatDamage };
        state = GameEngine.ApplyCombatDamage(state);

        state.GetPlayer(TestFactory.Player2Id).Life.Should().Be(17);
    }

    // =========================================================
    // Scenario: Deathtouch kills larger creature
    // =========================================================

    [Fact]
    public void Deathtouch_creature_kills_larger_blocker()
    {
        var deathtouchDef = TestFactory.MakeCreatureDef("Snake", 1, 1, keywords: KeywordAbility.Deathtouch);
        var bigDef = TestFactory.MakeCreatureDef("Giant", 5, 5);

        var attacker = TestFactory.MakePermanent(deathtouchDef, TestFactory.Player1Id, summoningSick: false);
        var blocker = TestFactory.MakePermanent(bigDef, TestFactory.Player2Id);

        var state = TestFactory.MakeTwoPlayerGame(Phase.Combat, Step.DeclareAttackers)
            .WithPermanent(attacker)
            .WithPermanent(blocker);

        state = GameEngine.DeclareAttackers(state, TestFactory.Player1Id, [attacker.PermanentId]);
        state = state with { CurrentStep = Step.DeclareBlockers };
        state = GameEngine.DeclareBlockers(state, TestFactory.Player2Id,
            new Dictionary<Guid, Guid> { [blocker.PermanentId] = attacker.PermanentId });
        state = state with { CurrentStep = Step.CombatDamage };
        state = GameEngine.ApplyCombatDamage(state);

        // Giant should die from deathtouch, snake dies from 5 damage
        state.Battlefield.Should().BeEmpty();
    }

    // =========================================================
    // Scenario: Player loses from combat damage
    // =========================================================

    [Fact]
    public void Player_loses_when_combat_damage_reduces_life_to_zero()
    {
        var def = TestFactory.MakeCreatureDef("Titan", 10, 10);
        var attacker = TestFactory.MakePermanent(def, TestFactory.Player1Id, summoningSick: false);

        var state = TestFactory.MakeTwoPlayerGame(Phase.Combat, Step.DeclareAttackers)
            .WithPermanent(attacker);

        // Set defending player to 5 life
        var p2 = state.GetPlayer(TestFactory.Player2Id) with { Life = 5 };
        state = state.UpdatePlayer(p2);

        state = GameEngine.DeclareAttackers(state, TestFactory.Player1Id, [attacker.PermanentId]);
        state = state with { CurrentStep = Step.DeclareBlockers };
        state = GameEngine.DeclareBlockers(state, TestFactory.Player2Id, new Dictionary<Guid, Guid>());
        state = state with { CurrentStep = Step.CombatDamage };
        state = GameEngine.ApplyCombatDamage(state);

        state.Result.Should().NotBe(GameResult.InProgress);
    }

    // =========================================================
    // Scenario: Priority flow through stack
    // =========================================================

    [Fact]
    public void Priority_passes_correctly_through_stack_resolution()
    {
        var instDef = TestFactory.MakeInstantDef("Shock", "R");
        var card = TestFactory.MakeCard(instDef, TestFactory.Player1Id);

        var state = MakeGameInMainPhase()
            .WithCardInHand(TestFactory.Player1Id, card)
            .WithMana(TestFactory.Player1Id, ManaColor.Red, 1);

        // Cast the instant
        state = GameEngine.CastSpell(state, TestFactory.Player1Id, card.CardId);
        state.PriorityPlayerId.Should().Be(TestFactory.Player1Id); // caster gets priority first

        // Active player passes
        state = GameEngine.PassPriority(state, TestFactory.Player1Id);
        state.PriorityPlayerId.Should().Be(TestFactory.Player2Id);

        // Opponent passes -- stack resolves
        state = GameEngine.PassPriority(state, TestFactory.Player2Id);
        state.Stack.IsEmpty.Should().BeTrue();
        state.PriorityPlayerId.Should().Be(TestFactory.Player1Id); // priority returns to active player
    }
}
