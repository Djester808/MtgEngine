using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using MtgEngine.Api.Controllers;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Domain.ValueObjects;
using Xunit;

namespace MtgEngine.Rules.Tests;

public class CardsControllerTests
{
    private static CardsController MakeController(Mock<IScryfallService> scryfallMock) =>
        new(scryfallMock.Object);

    private static Mock<IScryfallService> MockScryfall(
        CardDefinition[]? searchResult = null,
        SetSummaryDto[]? setsResult = null)
    {
        var mock = new Mock<IScryfallService>();
        mock.Setup(s => s.SearchAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(searchResult ?? []);
        mock.Setup(s => s.GetSetsAsync())
            .ReturnsAsync(setsResult ?? []);
        return mock;
    }

    private static CardDefinition MakeCardDef(string name = "Test", string manaCost = "1G") => new()
    {
        OracleId     = Guid.NewGuid().ToString(),
        Name         = name,
        ManaCost     = ManaCost.Parse(manaCost),
        CardTypes    = CardType.Creature,
        CastingSpeed = SpeedRestriction.Sorcery,
    };

    // ---- Search ----------------------------------------

    [Fact]
    public async Task Search_EmptyQuery_Returns400()
    {
        var controller = MakeController(MockScryfall());

        var result = await controller.Search("   ");

        result.Result.Should().BeOfType<BadRequestResult>();
    }

    [Fact]
    public async Task Search_ValidQuery_Returns200WithCards()
    {
        var defs = new[] { MakeCardDef("Lightning Bolt", "R"), MakeCardDef("Counterspell", "UU") };
        var controller = MakeController(MockScryfall(searchResult: defs));

        var result = await controller.Search("bolt");

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var cards = ok.Value.Should().BeOfType<CardDto[]>().Subject;
        cards.Should().HaveCount(2);
        cards.Select(c => c.Name).Should().Contain("Lightning Bolt");
    }

    [Fact]
    public async Task Search_PassesQueryAndDefaultParamsToService()
    {
        var mock = MockScryfall();
        var controller = MakeController(mock);

        await controller.Search("bolt");

        mock.Verify(s => s.SearchAsync("bolt", 60, 0, "name", "asc"), Times.Once);
    }

    [Fact]
    public async Task Search_PassesCustomOffsetAndSortToService()
    {
        var mock = MockScryfall();
        var controller = MakeController(mock);

        await controller.Search("fireball", limit: 30, offset: 60, sortBy: "cmc", sortDir: "desc");

        mock.Verify(s => s.SearchAsync("fireball", 30, 60, "cmc", "desc"), Times.Once);
    }

    [Fact]
    public async Task Search_ClampsLimitToMax60()
    {
        var mock = MockScryfall();
        var controller = MakeController(mock);

        await controller.Search("anything", limit: 200);

        mock.Verify(s => s.SearchAsync(
            It.IsAny<string>(), 60, It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Search_ClampsNegativeOffsetToZero()
    {
        var mock = MockScryfall();
        var controller = MakeController(mock);

        await controller.Search("anything", offset: -5);

        mock.Verify(s => s.SearchAsync(
            It.IsAny<string>(), It.IsAny<int>(), 0,
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Search_EmptyResults_ReturnsOkWithEmptyArray()
    {
        var controller = MakeController(MockScryfall(searchResult: []));

        var result = await controller.Search("xyzzy");

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var cards = ok.Value.Should().BeOfType<CardDto[]>().Subject;
        cards.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_MapsLegalitiesOntoCardDto()
    {
        var def = new CardDefinition
        {
            OracleId     = Guid.NewGuid().ToString(),
            Name         = "Force of Will",
            ManaCost     = ManaCost.Parse("3UU"),
            CardTypes    = CardType.Instant,
            CastingSpeed = SpeedRestriction.Instant,
            Legalities   = new Dictionary<string, string>
            {
                ["legacy"]  = "legal",
                ["vintage"] = "legal",
                ["modern"]  = "not_legal",
            },
        };
        var controller = MakeController(MockScryfall(searchResult: [def]));

        var result = await controller.Search("force");

        var ok    = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var cards = ok.Value.Should().BeOfType<CardDto[]>().Subject;
        cards[0].Legalities.Should().ContainKey("legacy").WhoseValue.Should().Be("legal");
        cards[0].Legalities.Should().ContainKey("modern").WhoseValue.Should().Be("not_legal");
    }

    // ---- GetSets ----------------------------------------

    [Fact]
    public async Task GetSets_Returns200WithSets()
    {
        var sets = new SetSummaryDto[]
        {
            new("NEO", "Kamigawa: Neon Dynasty", 302),
            new("MOM", "March of the Machine", 281),
        };
        var controller = MakeController(MockScryfall(setsResult: sets));

        var result = await controller.GetSets();

        var ok   = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var dtos = ok.Value.Should().BeOfType<SetSummaryDto[]>().Subject;
        dtos.Should().HaveCount(2);
        dtos.Select(s => s.Code).Should().Contain("NEO");
    }

    [Fact]
    public async Task GetSets_WhenNoSets_ReturnsEmptyArray()
    {
        var controller = MakeController(MockScryfall(setsResult: []));

        var result = await controller.GetSets();

        var ok   = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var dtos = ok.Value.Should().BeOfType<SetSummaryDto[]>().Subject;
        dtos.Should().BeEmpty();
    }
}
