using FluentAssertions;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.SBA;
using Xunit;

namespace MtgEngine.Rules.Tests;

public class StateBasedActionTests
{
    // =========================================================
    // Life loss
    // =========================================================

    [Fact]
    public void Player_with_zero_life_loses_the_game()
    {
        var state = TestFactory.MakeTwoPlayerGame();
        var p1 = state.GetPlayer(TestFactory.Player1Id);
        state = state.UpdatePlayer(p1 with { Life = 0 });

        var (result, _) = StateBasedActions.Apply(state);

        result.Result.Should().NotBe(GameResult.InProgress);
    }

    [Fact]
    public void Player_with_negative_life_loses_the_game()
    {
        var state = TestFactory.MakeTwoPlayerGame();
        var p1 = state.GetPlayer(TestFactory.Player1Id);
        state = state.UpdatePlayer(p1 with { Life = -5 });

        var (result, log) = StateBasedActions.Apply(state);

        result.Result.Should().NotBe(GameResult.InProgress);
        log.Should().NotBeEmpty();
    }

    [Fact]
    public void Player_with_positive_life_does_not_lose()
    {
        var state = TestFactory.MakeTwoPlayerGame();

        var (result, _) = StateBasedActions.Apply(state);

        result.Result.Should().Be(GameResult.InProgress);
    }

    // =========================================================
    // Poison
    // =========================================================

    [Fact]
    public void Player_with_ten_poison_loses()
    {
        var state = TestFactory.MakeTwoPlayerGame();
        var p1 = state.GetPlayer(TestFactory.Player1Id);
        state = state.UpdatePlayer(p1 with { PoisonCounters = 10 });

        var (result, _) = StateBasedActions.Apply(state);

        result.Result.Should().NotBe(GameResult.InProgress);
    }

    [Fact]
    public void Player_with_nine_poison_does_not_lose()
    {
        var state = TestFactory.MakeTwoPlayerGame();
        var p1 = state.GetPlayer(TestFactory.Player1Id);
        state = state.UpdatePlayer(p1 with { PoisonCounters = 9 });

        var (result, _) = StateBasedActions.Apply(state);

        result.Result.Should().Be(GameResult.InProgress);
    }

    // =========================================================
    // Lethal damage
    // =========================================================

    [Fact]
    public void Creature_with_lethal_damage_dies()
    {
        var def = TestFactory.MakeCreatureDef(power: 2, toughness: 2);
        var permanent = TestFactory.MakePermanent(def, TestFactory.Player1Id, damage: 2);
        var state = TestFactory.MakeTwoPlayerGame()
            .WithPermanent(permanent);

        var (result, log) = StateBasedActions.Apply(state);

        result.Battlefield.Should().BeEmpty();
        var p1 = result.GetPlayer(TestFactory.Player1Id);
        p1.Graveyard.Should().HaveCount(1);
        log.Should().Contain(l => l.Contains("lethal damage"));
    }

    [Fact]
    public void Creature_with_damage_less_than_toughness_survives()
    {
        var def = TestFactory.MakeCreatureDef(power: 2, toughness: 3);
        var permanent = TestFactory.MakePermanent(def, TestFactory.Player1Id, damage: 2);
        var state = TestFactory.MakeTwoPlayerGame()
            .WithPermanent(permanent);

        var (result, _) = StateBasedActions.Apply(state);

        result.Battlefield.Should().HaveCount(1);
    }

    [Fact]
    public void Creature_with_zero_toughness_dies()
    {
        var def = TestFactory.MakeCreatureDef(power: 2, toughness: 2);
        var permanent = TestFactory.MakePermanent(def, TestFactory.Player1Id,
            counters: new() { [CounterType.MinusOneMinusOne] = 2 });
        var state = TestFactory.MakeTwoPlayerGame()
            .WithPermanent(permanent);

        var (result, log) = StateBasedActions.Apply(state);

        result.Battlefield.Should().BeEmpty();
        log.Should().Contain(l => l.Contains("toughness 0"));
    }

    [Fact]
    public void Indestructible_creature_with_lethal_damage_survives()
    {
        var def = TestFactory.MakeCreatureDef(power: 2, toughness: 2, keywords: KeywordAbility.Indestructible);
        var permanent = TestFactory.MakePermanent(def, TestFactory.Player1Id, damage: 5);
        var state = TestFactory.MakeTwoPlayerGame()
            .WithPermanent(permanent);

        var (result, _) = StateBasedActions.Apply(state);

        result.Battlefield.Should().HaveCount(1);
    }

    // =========================================================
    // Legend rule
    // =========================================================

    [Fact]
    public void Two_legendary_creatures_with_same_name_triggers_legend_rule()
    {
        var def = new MtgEngine.Domain.Models.CardDefinition
        {
            OracleId = "legendary-test",
            Name = "Legendary Hero",
            ManaCost = MtgEngine.Domain.ValueObjects.ManaCost.Parse("2W"),
            CardTypes = CardType.Creature,
            Subtypes = ["Human", "Warrior"],
            Supertypes = ["Legendary"],
            Power = 3,
            Toughness = 3,
            CastingSpeed = SpeedRestriction.Sorcery,
        };

        var p1 = TestFactory.MakePermanent(def, TestFactory.Player1Id);
        var p2 = TestFactory.MakePermanent(def, TestFactory.Player1Id);
        var state = TestFactory.MakeTwoPlayerGame()
            .WithPermanent(p1)
            .WithPermanent(p2);

        var (result, log) = StateBasedActions.Apply(state);

        result.Battlefield.Should().HaveCount(1);
        log.Should().Contain(l => l.Contains("legend rule"));
    }

    // =========================================================
    // Planeswalker loyalty
    // =========================================================

    [Fact]
    public void Planeswalker_with_zero_loyalty_dies()
    {
        var def = new MtgEngine.Domain.Models.CardDefinition
        {
            OracleId = "pw-test",
            Name = "Test Planeswalker",
            ManaCost = MtgEngine.Domain.ValueObjects.ManaCost.Parse("3W"),
            CardTypes = CardType.Planeswalker,
            StartingLoyalty = 3,
            CastingSpeed = SpeedRestriction.Sorcery,
        };

        var permanent = TestFactory.MakePermanent(def, TestFactory.Player1Id,
            counters: new() { [CounterType.Loyalty] = 0 });
        var state = TestFactory.MakeTwoPlayerGame()
            .WithPermanent(permanent);

        var (result, log) = StateBasedActions.Apply(state);

        result.Battlefield.Should().BeEmpty();
    }
}
