using FluentAssertions;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Mapping;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Domain.ValueObjects;
using Xunit;

namespace MtgEngine.Rules.Tests;

public class DomainMapperTests
{
    // ---- CardDefinition → CardDto ----------------------------------------

    [Fact]
    public void ToDto_Card_MapsNameCardIdAndOwnerId()
    {
        var def = TestFactory.MakeCreatureDef("Bear");
        var cardId = Guid.NewGuid();
        var ownerId = TestFactory.Player1Id;

        var dto = DomainMapper.ToDto(def, cardId, ownerId);

        dto.Name.Should().Be("Bear");
        dto.CardId.Should().Be(cardId.ToString());
        dto.OwnerId.Should().Be(ownerId.ToString());
    }

    [Fact]
    public void ToDto_Card_MapsColoredManaCostAndManaValue()
    {
        var def = TestFactory.MakeCreatureDef(manaCost: "2WW");

        var dto = DomainMapper.ToDto(def, Guid.NewGuid(), Guid.NewGuid());

        dto.ManaCost.Should().Contain("W");
        dto.ManaValue.Should().Be(4);
    }

    [Fact]
    public void ToDto_LandCard_HasEmptyManaCostString()
    {
        var def = TestFactory.MakeLandDef("Forest");

        var dto = DomainMapper.ToDto(def, Guid.NewGuid(), Guid.NewGuid());

        dto.ManaCost.Should().BeEmpty();
        dto.ManaValue.Should().Be(0);
    }

    [Fact]
    public void ToDto_Card_MapsLandToLandCardType()
    {
        var def = TestFactory.MakeLandDef();

        var dto = DomainMapper.ToDto(def, Guid.NewGuid(), Guid.NewGuid());

        dto.CardTypes.Should().ContainSingle().Which.Should().Be(CardTypeDto.Land);
    }

    [Fact]
    public void ToDto_Card_MapsCreatureSubtypes()
    {
        var def = TestFactory.MakeCreatureDef();

        var dto = DomainMapper.ToDto(def, Guid.NewGuid(), Guid.NewGuid());

        dto.CardTypes.Should().Contain(CardTypeDto.Creature);
        dto.Subtypes.Should().Contain("Beast");
    }

    [Fact]
    public void ToDto_Card_MapsFlavorTextArtistAndSetCode()
    {
        var def = new CardDefinition
        {
            OracleId      = Guid.NewGuid().ToString(),
            Name          = "Test",
            ManaCost      = ManaCost.Parse("1G"),
            CardTypes     = CardType.Creature,
            Subtypes      = ["Beast"],
            Power         = 2,
            Toughness     = 2,
            CastingSpeed  = SpeedRestriction.Sorcery,
            FlavorText    = "It lurks in the shadows.",
            Artist        = "Jane Painter",
            SetCode       = "m21",
        };

        var dto = DomainMapper.ToDto(def, Guid.NewGuid(), Guid.NewGuid());

        dto.FlavorText.Should().Be("It lurks in the shadows.");
        dto.Artist.Should().Be("Jane Painter");
        dto.SetCode.Should().Be("m21");
    }

    [Fact]
    public void ToDto_Card_NullFlavorFieldsPassThrough()
    {
        var def = TestFactory.MakeCreatureDef();

        var dto = DomainMapper.ToDto(def, Guid.NewGuid(), Guid.NewGuid());

        dto.FlavorText.Should().BeNull();
        dto.Artist.Should().BeNull();
        dto.SetCode.Should().BeNull();
    }

    // ---- Permanent → PermanentDto ----------------------------------------

    [Fact]
    public void ToDto_Permanent_MapsTappedStateAndControllerId()
    {
        var perm = TestFactory.MakePermanent(
            TestFactory.MakeCreatureDef(),
            TestFactory.Player1Id,
            tapped: true);

        var dto = DomainMapper.ToDto(perm);

        dto.IsTapped.Should().BeTrue();
        dto.ControllerId.Should().Be(TestFactory.Player1Id.ToString());
    }

    [Fact]
    public void ToDto_Permanent_MapsDamageMarked()
    {
        var perm = TestFactory.MakePermanent(
            TestFactory.MakeCreatureDef(),
            TestFactory.Player1Id,
            damage: 3);

        var dto = DomainMapper.ToDto(perm);

        dto.DamageMarked.Should().Be(3);
    }

    [Fact]
    public void ToDto_Permanent_MapsEffectivePowerAndToughness()
    {
        var perm = TestFactory.MakePermanent(
            TestFactory.MakeCreatureDef(power: 3, toughness: 5),
            TestFactory.Player1Id);

        var dto = DomainMapper.ToDto(perm);

        dto.EffectivePower.Should().Be(3);
        dto.EffectiveToughness.Should().Be(5);
    }

    [Fact]
    public void ToDto_Permanent_MapsSummoningSickness()
    {
        var perm = TestFactory.MakePermanent(
            TestFactory.MakeCreatureDef(),
            TestFactory.Player1Id,
            summoningSick: true);

        var dto = DomainMapper.ToDto(perm);

        dto.HasSummoningSickness.Should().BeTrue();
    }

    [Fact]
    public void ToDto_Permanent_MapsCounters()
    {
        var perm = TestFactory.MakePermanent(
            TestFactory.MakeCreatureDef(),
            TestFactory.Player1Id,
            counters: new Dictionary<CounterType, int> { [CounterType.PlusOnePlusOne] = 2 });

        var dto = DomainMapper.ToDto(perm);

        dto.Counters["PlusOnePlusOne"].Should().Be(2);
    }

    // ---- ToCardTypeDto ----------------------------------------

    [Fact]
    public void ToCardTypeDto_MultiTypeFlags_ReturnsBothTypes()
    {
        var flags = CardType.Creature | CardType.Artifact;

        var result = DomainMapper.ToCardTypeDto(flags);

        result.Should().Contain(CardTypeDto.Creature);
        result.Should().Contain(CardTypeDto.Artifact);
        result.Should().HaveCount(2);
    }

    // ---- Enum conversions ----------------------------------------

    [Theory]
    [InlineData(Phase.Beginning,      PhaseDto.Beginning)]
    [InlineData(Phase.PreCombatMain,  PhaseDto.PreCombatMain)]
    [InlineData(Phase.Combat,         PhaseDto.Combat)]
    [InlineData(Phase.PostCombatMain, PhaseDto.PostCombatMain)]
    [InlineData(Phase.Ending,         PhaseDto.Ending)]
    public void ToDto_Phase_MapsAllValues(Phase input, PhaseDto expected)
    {
        DomainMapper.ToDto(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(Step.Untap,           StepDto.Untap)]
    [InlineData(Step.Draw,            StepDto.Draw)]
    [InlineData(Step.Main,            StepDto.Main)]
    [InlineData(Step.DeclareAttackers,StepDto.DeclareAttackers)]
    [InlineData(Step.CombatDamage,    StepDto.CombatDamage)]
    [InlineData(Step.Cleanup,         StepDto.Cleanup)]
    public void ToDto_Step_MapsValues(Step input, StepDto expected)
    {
        DomainMapper.ToDto(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(ManaColor.White,     ManaColorDto.W)]
    [InlineData(ManaColor.Blue,      ManaColorDto.U)]
    [InlineData(ManaColor.Black,     ManaColorDto.B)]
    [InlineData(ManaColor.Red,       ManaColorDto.R)]
    [InlineData(ManaColor.Green,     ManaColorDto.G)]
    [InlineData(ManaColor.Colorless, ManaColorDto.C)]
    public void ToDto_ManaColor_MapsAllColors(ManaColor input, ManaColorDto expected)
    {
        DomainMapper.ToDto(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(GameResult.InProgress,  GameResultDto.InProgress)]
    [InlineData(GameResult.Player1Wins, GameResultDto.Player1Wins)]
    [InlineData(GameResult.Player2Wins, GameResultDto.Player2Wins)]
    [InlineData(GameResult.Draw,        GameResultDto.Draw)]
    public void ToDto_GameResult_MapsAllValues(GameResult input, GameResultDto expected)
    {
        DomainMapper.ToDto(input).Should().Be(expected);
    }

    // ---- ToDiff ----------------------------------------

    [Fact]
    public void ToDiff_TappedPermanent_AppearsInChangedList()
    {
        var perm = TestFactory.MakePermanent(TestFactory.MakeCreatureDef(), TestFactory.Player1Id);
        var before = TestFactory.MakeTwoPlayerGame().WithPermanent(perm);
        var after  = TestFactory.MakeTwoPlayerGame().WithPermanent(perm with { IsTapped = true });

        var diff = DomainMapper.ToDiff(before, after, TestFactory.Player1Id);

        diff.ChangedPermanents.Should().HaveCount(1);
        diff.ChangedPermanents[0].IsTapped.Should().BeTrue();
    }

    [Fact]
    public void ToDiff_RemovedPermanent_AppearsInRemovedIds()
    {
        var perm = TestFactory.MakePermanent(TestFactory.MakeCreatureDef(), TestFactory.Player1Id);
        var before = TestFactory.MakeTwoPlayerGame().WithPermanent(perm);
        var after  = TestFactory.MakeTwoPlayerGame();

        var diff = DomainMapper.ToDiff(before, after, TestFactory.Player1Id);

        diff.RemovedPermanentIds.Should().Contain(perm.PermanentId.ToString());
        diff.ChangedPermanents.Should().BeEmpty();
    }

    [Fact]
    public void ToDiff_UnchangedPermanent_NotInAnyList()
    {
        var perm  = TestFactory.MakePermanent(TestFactory.MakeCreatureDef(), TestFactory.Player1Id);
        var state = TestFactory.MakeTwoPlayerGame().WithPermanent(perm);

        var diff = DomainMapper.ToDiff(state, state, TestFactory.Player1Id);

        diff.ChangedPermanents.Should().BeEmpty();
        diff.RemovedPermanentIds.Should().BeEmpty();
    }

    [Fact]
    public void ToDiff_NewPermanent_AppearsInChangedList()
    {
        var perm   = TestFactory.MakePermanent(TestFactory.MakeCreatureDef(), TestFactory.Player1Id);
        var before = TestFactory.MakeTwoPlayerGame();
        var after  = TestFactory.MakeTwoPlayerGame().WithPermanent(perm);

        var diff = DomainMapper.ToDiff(before, after, TestFactory.Player1Id);

        diff.ChangedPermanents.Should().HaveCount(1);
        diff.ChangedPermanents[0].PermanentId.Should().Be(perm.PermanentId.ToString());
    }
}
