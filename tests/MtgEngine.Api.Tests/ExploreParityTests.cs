using System.Text.Json;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Api.Services.Community;
namespace MtgEngine.Api.Tests;

public sealed class ExploreParityTests
{
    [Fact]
    public void DirectoryContinuationPagesUseRootCardviews()
    {
        using var json = JsonDocument.Parse("""{"cardviews":[{"name":"Sidar Jabari of Zhalfir","num_decks":17840,"rank":101,"sanitized":"sidar-jabari-of-zhalfir"}],"is_paginated":true}""");
        var row = Assert.Single(EdhrecTopCommandersParser.Parse(json));
        Assert.Equal(101, row.Rank);
        Assert.Equal(17840, row.DeckCount);
    }
    [Fact]
    public void DirectoryFiltersBeforePagingAndKeepsMissingRanksLast()
    {
        ExploreEntry Entry(string name, ManaColorDto[] colors, int? rank, int? decks) => new(name, new CardDto { Name = name, ColorIdentity = colors }, Decks: decks, Kind: "commander", Rank: rank);
        var rows = new[] { Entry("White", [ManaColorDto.W], null, null), Entry("Azorius", [ManaColorDto.W, ManaColorDto.U], 2, 20), Entry("Blue", [ManaColorDto.U], 3, 10) };
        var options = new ExploreDirectoryOptions(Identity: "WU", Mode: "exact", Sort: "rank", Dir: "asc", PageSize: 1);
        var result = ExploreDirectoryService.Run(rows, options, DateTimeOffset.UtcNow);
        Assert.Equal(1, result.Total);
        Assert.Equal("Azorius", Assert.Single(result.Commanders).Name);
        result = ExploreDirectoryService.Run(rows, options with { Mode = "within", Page = 3 }, DateTimeOffset.UtcNow);
        Assert.Equal(3, result.Total);
        Assert.Null(Assert.Single(result.Commanders).Rank);
        Assert.Throws<InvalidRequestException>(() => ExploreDirectoryService.Validate(options with { Window = "bad" }));
    }
    [Fact]
    public void InsightsReadOnlyActualTimelineRankAndQuantityAwareHybridPips()
    {
        using var json = JsonDocument.Parse("""{"savedate_counts":{"2026-02-01":2,"2026-02-02":3},"panels":{"rank_over_time":{"2026-01-01":{"rank":5}},"combocounts":[]}}""");
        var charts = ExploreCharts.Read(json.RootElement);
        Assert.Equal(5, Assert.Single(charts, c => c.Title == "Decks by month").Points[0].Value);
        Assert.Equal(5, Assert.Single(charts, c => c.Title == "Rank over time").Points[0].Value);
        var sections = new[] { new ExploreSection("creature", "Creatures", [new ExploreEntry("Hybrid", new CardDto { OracleId = "h", ManaCost = "{G/W}{W/P}{2/U}{X}", ManaValue = 0, CardTypes = [CardTypeDto.Creature] }, Quantity: 2)]) };
        var shape = ExploreDeckShape.Read(sections, true);
        var pips = Assert.Single(shape, c => c.Title == "Average deck mana pips");
        Assert.Equal(4, Assert.Single(pips.Points, p => p.Label == "W").Value);
        Assert.Equal(2, Assert.Single(pips.Points, p => p.Label == "U").Value);
        Assert.Equal("0", Assert.Single(shape, c => c.Title == "Average deck mana curve").Points[0].Label);
        Assert.Empty(ExploreCharts.Read(JsonDocument.Parse("{}").RootElement));
        Assert.Empty(ExploreDeckShape.Read([], true));
    }
}
