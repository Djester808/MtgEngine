using FluentAssertions;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.ValueObjects;
using Xunit;

namespace MtgEngine.Rules.Tests;

public class ZoneManagerTests
{
    // =========================================================
    // Playing lands
    // =========================================================

    [Fact]
    public void Can_play_land_during_main_phase_with_priority()
    {
        var landDef = TestFactory.MakeLandDef("Forest");
        var land = TestFactory.MakeCard(landDef, TestFactory.Player1Id);
        var state = TestFactory.MakeTwoPlayerGame(Phase.PreCombatMain, Step.Main)
            .WithCardInHand(TestFactory.Player1Id, land);

        var result = ZoneManager.PlayLand(state, TestFactory.Player1Id, land.CardId);

        result.Battlefield.Should().HaveCount(1);
        result.GetPlayer(TestFactory.Player1Id).Hand.Should().BeEmpty();
        result.GetPlayer(TestFactory.Player1Id).HasLandPlayedThisTurn.Should().BeTrue();
    }

    [Fact]
    public void Cannot_play_two_lands_in_one_turn()
    {
        var landDef = TestFactory.MakeLandDef("Forest");
        var land1 = TestFactory.MakeCard(landDef, TestFactory.Player1Id);
        var land2 = TestFactory.MakeCard(landDef, TestFactory.Player1Id);
        var state = TestFactory.MakeTwoPlayerGame(Phase.PreCombatMain, Step.Main)
            .WithCardInHand(TestFactory.Player1Id, land1)
            .WithCardInHand(TestFactory.Player1Id, land2);

        var afterFirst = ZoneManager.PlayLand(state, TestFactory.Player1Id, land1.CardId);
        var act = () => ZoneManager.PlayLand(afterFirst, TestFactory.Player1Id, land2.CardId);

        act.Should().Throw<InvalidOperationException>().WithMessage("*one land*");
    }

    [Fact]
    public void Cannot_play_land_during_combat()
    {
        var landDef = TestFactory.MakeLandDef("Forest");
        var land = TestFactory.MakeCard(landDef, TestFactory.Player1Id);
        var state = TestFactory.MakeTwoPlayerGame(Phase.Combat, Step.BeginningOfCombat)
            .WithCardInHand(TestFactory.Player1Id, land);

        var act = () => ZoneManager.PlayLand(state, TestFactory.Player1Id, land.CardId);

        act.Should().Throw<InvalidOperationException>();
    }

    // =========================================================
    // Tapping for mana
    // =========================================================

    [Fact]
    public void Tapping_forest_adds_green_mana()
    {
        var landDef = TestFactory.MakeLandDef("Forest");
        var land = TestFactory.MakePermanent(landDef, TestFactory.Player1Id);
        var state = TestFactory.MakeTwoPlayerGame()
            .WithPermanent(land);

        var result = ZoneManager.TapLandForMana(state, TestFactory.Player1Id, land.PermanentId);

        result.GetPermanent(land.PermanentId).IsTapped.Should().BeTrue();
        result.GetPlayer(TestFactory.Player1Id).ManaPool.Amounts[ManaColor.Green].Should().Be(1);
    }

    [Fact]
    public void Cannot_tap_already_tapped_land()
    {
        var landDef = TestFactory.MakeLandDef("Forest");
        var land = TestFactory.MakePermanent(landDef, TestFactory.Player1Id, tapped: true);
        var state = TestFactory.MakeTwoPlayerGame().WithPermanent(land);

        var act = () => ZoneManager.TapLandForMana(state, TestFactory.Player1Id, land.PermanentId);

        act.Should().Throw<InvalidOperationException>().WithMessage("*already tapped*");
    }

    // =========================================================
    // Casting spells
    // =========================================================

    [Fact]
    public void Can_cast_creature_at_sorcery_speed_during_main_phase()
    {
        var creatureDef = TestFactory.MakeCreatureDef("Grizzly Bears", 2, 2, "1G");
        var card = TestFactory.MakeCard(creatureDef, TestFactory.Player1Id);
        var state = TestFactory.MakeTwoPlayerGame(Phase.PreCombatMain, Step.Main)
            .WithCardInHand(TestFactory.Player1Id, card)
            .WithMana(TestFactory.Player1Id, ManaColor.Green, 1)
            .WithMana(TestFactory.Player1Id, ManaColor.Green, 1); // 2 mana total, 1G cost

        var result = ZoneManager.CastSpell(state, TestFactory.Player1Id, card.CardId);

        result.Stack.IsEmpty.Should().BeFalse();
        result.GetPlayer(TestFactory.Player1Id).Hand.Should().BeEmpty();
    }

    [Fact]
    public void Cannot_cast_creature_without_enough_mana()
    {
        var creatureDef = TestFactory.MakeCreatureDef("Expensive Creature", 5, 5, "3GGG");
        var card = TestFactory.MakeCard(creatureDef, TestFactory.Player1Id);
        var state = TestFactory.MakeTwoPlayerGame(Phase.PreCombatMain, Step.Main)
            .WithCardInHand(TestFactory.Player1Id, card)
            .WithMana(TestFactory.Player1Id, ManaColor.Green, 1);

        var act = () => ZoneManager.CastSpell(state, TestFactory.Player1Id, card.CardId);

        act.Should().Throw<InvalidOperationException>().WithMessage("*mana*");
    }

    [Fact]
    public void Resolving_creature_spell_puts_permanent_on_battlefield()
    {
        var creatureDef = TestFactory.MakeCreatureDef("Grizzly Bears", 2, 2, "1G");
        var card = TestFactory.MakeCard(creatureDef, TestFactory.Player1Id);
        var state = TestFactory.MakeTwoPlayerGame(Phase.PreCombatMain, Step.Main)
            .WithCardInHand(TestFactory.Player1Id, card)
            .WithMana(TestFactory.Player1Id, ManaColor.Green, 2);

        var afterCast = ZoneManager.CastSpell(state, TestFactory.Player1Id, card.CardId);
        var afterResolve = ZoneManager.ResolveTopOfStack(afterCast);

        afterResolve.Battlefield.Should().HaveCount(1);
        afterResolve.Battlefield[0].Name.Should().Be("Grizzly Bears");
        afterResolve.Stack.IsEmpty.Should().BeTrue();
    }

    // =========================================================
    // Permanent removal
    // =========================================================

    [Fact]
    public void Destroying_permanent_sends_it_to_graveyard()
    {
        var def = TestFactory.MakeCreatureDef();
        var permanent = TestFactory.MakePermanent(def, TestFactory.Player1Id);
        var state = TestFactory.MakeTwoPlayerGame().WithPermanent(permanent);

        var result = ZoneManager.DestroyPermanent(state, permanent.PermanentId);

        result.Battlefield.Should().BeEmpty();
        result.GetPlayer(TestFactory.Player1Id).Graveyard.Should().HaveCount(1);
    }

    [Fact]
    public void Indestructible_permanent_survives_destroy()
    {
        var def = TestFactory.MakeCreatureDef(keywords: KeywordAbility.Indestructible);
        var permanent = TestFactory.MakePermanent(def, TestFactory.Player1Id);
        var state = TestFactory.MakeTwoPlayerGame().WithPermanent(permanent);

        var result = ZoneManager.DestroyPermanent(state, permanent.PermanentId);

        result.Battlefield.Should().HaveCount(1);
    }

    [Fact]
    public void Exiling_permanent_sends_it_to_exile()
    {
        var def = TestFactory.MakeCreatureDef();
        var permanent = TestFactory.MakePermanent(def, TestFactory.Player1Id);
        var state = TestFactory.MakeTwoPlayerGame().WithPermanent(permanent);

        var result = ZoneManager.ExilePermanent(state, permanent.PermanentId);

        result.Battlefield.Should().BeEmpty();
        result.GetPlayer(TestFactory.Player1Id).Exile.Should().HaveCount(1);
    }

    // =========================================================
    // Untapping lands
    // =========================================================

    [Fact]
    public void Untap_land_restores_tapped_state_and_removes_mana()
    {
        var landDef = TestFactory.MakeLandDef("Forest");
        var land = TestFactory.MakePermanent(landDef, TestFactory.Player1Id, tapped: true);
        var state = TestFactory.MakeTwoPlayerGame()
            .WithPermanent(land)
            .WithMana(TestFactory.Player1Id, ManaColor.Green);

        var result = ZoneManager.UntapLand(state, TestFactory.Player1Id, land.PermanentId);

        result.GetPermanent(land.PermanentId).IsTapped.Should().BeFalse();
        result.GetPlayer(TestFactory.Player1Id).ManaPool.Total.Should().Be(0);
    }

    [Fact]
    public void Untap_land_removes_only_the_correct_color()
    {
        var landDef = TestFactory.MakeLandDef("Forest");
        var land = TestFactory.MakePermanent(landDef, TestFactory.Player1Id, tapped: true);
        var state = TestFactory.MakeTwoPlayerGame()
            .WithPermanent(land)
            .WithMana(TestFactory.Player1Id, ManaColor.Green)
            .WithMana(TestFactory.Player1Id, ManaColor.Red);

        var result = ZoneManager.UntapLand(state, TestFactory.Player1Id, land.PermanentId);

        result.GetPlayer(TestFactory.Player1Id).ManaPool.Amounts.ContainsKey(ManaColor.Green).Should().BeFalse();
        result.GetPlayer(TestFactory.Player1Id).ManaPool.Amounts[ManaColor.Red].Should().Be(1);
    }

    [Fact]
    public void Cannot_untap_land_that_is_not_tapped()
    {
        var landDef = TestFactory.MakeLandDef("Forest");
        var land = TestFactory.MakePermanent(landDef, TestFactory.Player1Id, tapped: false);
        var state = TestFactory.MakeTwoPlayerGame().WithPermanent(land);

        var act = () => ZoneManager.UntapLand(state, TestFactory.Player1Id, land.PermanentId);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not tapped*");
    }

    [Fact]
    public void Cannot_untap_land_controlled_by_another_player()
    {
        var landDef = TestFactory.MakeLandDef("Forest");
        var land = TestFactory.MakePermanent(landDef, TestFactory.Player1Id, tapped: true);
        var state = TestFactory.MakeTwoPlayerGame()
            .WithPermanent(land)
            .WithMana(TestFactory.Player1Id, ManaColor.Green);

        var act = () => ZoneManager.UntapLand(state, TestFactory.Player2Id, land.PermanentId);

        act.Should().Throw<InvalidOperationException>().WithMessage("*control*");
    }

    [Fact]
    public void Cannot_untap_land_when_mana_already_spent()
    {
        var landDef = TestFactory.MakeLandDef("Forest");
        var land = TestFactory.MakePermanent(landDef, TestFactory.Player1Id, tapped: true);
        var state = TestFactory.MakeTwoPlayerGame()
            .WithPermanent(land); // no mana in pool

        var act = () => ZoneManager.UntapLand(state, TestFactory.Player1Id, land.PermanentId);

        act.Should().Throw<InvalidOperationException>().WithMessage("*spent*");
    }

    [Fact]
    public void Cannot_untap_a_non_land_permanent()
    {
        var creatureDef = TestFactory.MakeCreatureDef();
        var creature = TestFactory.MakePermanent(creatureDef, TestFactory.Player1Id, tapped: true);
        var state = TestFactory.MakeTwoPlayerGame().WithPermanent(creature);

        var act = () => ZoneManager.UntapLand(state, TestFactory.Player1Id, creature.PermanentId);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a land*");
    }
}
