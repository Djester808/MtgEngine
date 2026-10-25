using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;
namespace MtgEngine.Api.Tests;

public sealed class CollectionReadTests
{
    private sealed class CountingReader : ICollectionReader
    {
        public int Reads;
        public Task<CollectionDetailDto?> GetCollectionAsync(Guid collectionId, string userId)
        {
            Reads++;
            return Task.FromResult<CollectionDetailDto?>(Fixture());
        }
    }

    [Fact]
    public async Task LaterPagesReuseBoundedReadSnapshotAndMutationsInvalidateIt()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using var db = new MtgEngineDbContext(new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var collection = new Collection("owner", "Inventory");
        db.Collections.Add(collection);
        await db.SaveChangesAsync();
        using var cache = new CollectionReadCache();
        var reader = new CountingReader();
        var service = new CollectionReadService(db, reader, new Lookup(), cache);
        await service.PageAsync(collection.Id, "owner", new(), default);
        await service.PageAsync(collection.Id, "owner", new() { Page = 2 }, default);
        Assert.Equal(1, reader.Reads);
        collection.UpdatedAt = collection.UpdatedAt.AddSeconds(1);
        await db.SaveChangesAsync();
        await service.PageAsync(collection.Id, "owner", new(), default);
        Assert.Equal(2, reader.Reads);
        Assert.Null(await service.PageAsync(collection.Id, "stranger", new(), default));
    }

    private sealed class Lookup : StubScryfallService { public override Task<CardDefinition?> GetByOracleIdAsync(string oracleId) => Task.FromResult<CardDefinition?>(null); }
    private static CollectionDetailDto Fixture(int count = 205) => new()
    {
        Cards = Enumerable.Range(0, count).Select(i => new CollectionCardDto
        {
            Id = Guid.NewGuid(),
            OracleId = "oracle-" + i,
            Quantity = 1,
            QuantityEtched = i == 204 ? 3 : 0,
            Language = i == 204 ? "ja" : "en",
            AddedAt = new DateTime(2026, 10, 1),
            FinishData = i == 204 ? new() { ["etched"] = new("LP", ["trade"], new DateTime(2025, 1, 1), 1m) } : [],
            CardDetails = new CardDto { Name = $"Card {i:D3}", ManaValue = i % 8, SetCode = i == 204 ? "LAST" : "FIRST", Rarity = "common", CardTypes = [CardTypeDto.Creature], ColorIdentity = [ManaColorDto.R], Prices = new() { Usd = 2, UsdEtched = 4 } }
        }).ToArray()
    };

    [Fact]
    public void Page_is_bounded_and_summaries_cover_every_row()
    {
        var col = Fixture();
        var first = CollectionReadService.Project(col, new());
        Assert.Equal(100, first.Cards.Length);
        Assert.Equal(205, first.PageData!.Total);
        Assert.Equal(208, first.PageData.Valuation.Copies);
        Assert.Equal(422, first.PageData.Valuation.Value);
        Assert.Equal(10, first.PageData.Insights.Length);
        Assert.Contains(first.PageData.SetOptions, s => s.Value == "last");
        Assert.Equal(208, first.PageData.Insights.Single(s => s.Title == "Mana curve").Points.Sum(p => p.Value));
        var last = CollectionReadService.Project(col, new() { Page = 3 });
        Assert.Equal(5, last.Cards.Length);
        Assert.Equal(3, last.PageData!.Page);
        Assert.Equal(first.PageData.Valuation, last.PageData.Valuation);
        Assert.Equal(1, CollectionReadService.Project(col with { Cards = [] }, new() { Page = 999 }).PageData!.Page);
    }
    [Fact]
    public void Global_filters_and_order_run_before_paging_and_finish_metadata_stays_independent()
    {
        var col = Fixture();
        var result = CollectionReadService.Project(col, new() { Finish = "etched", Condition = "LP", Tag = "trade", Language = "ja", DuplicatesOnly = true, Set = "last", Colors = ["R"], Types = ["Creature"], Rarities = ["common"], Cmc = "4", Page = 3 });
        Assert.Equal("oracle-204", Assert.Single(result.Cards).OracleId);
        Assert.Equal(1, result.PageData!.Page);
        Assert.Empty(CollectionReadService.Project(col, new() { Finish = "nonfoil", Tag = "trade" }).Cards);
        Assert.Equal("oracle-204", CollectionReadService.Project(col, new() { Sort = "quantity", SortDir = "desc" }).Cards[0].OracleId);
        Assert.Equal("oracle-204", Assert.Single(CollectionReadService.Project(col, new() { Query = "oracle-204" }).Cards).OracleId);
    }
    [Fact]
    public void Section_order_is_global_before_paging_and_query_cancellation_is_observed()
    {
        var col = Fixture();
        var cmc = CollectionReadService.Project(col, new() { Group = "cmc", PageSize = 20 });
        Assert.All(cmc.Cards, row => Assert.Equal(0, row.CardDetails!.ManaValue));
        var last = CollectionReadService.Project(col, new() { Group = "cmc", Page = 11, PageSize = 20 });
        Assert.All(last.Cards, row => Assert.Equal(7, row.CardDetails!.ManaValue));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CollectionReadService.Project(col, new(), cancellation.Token));
    }
    [Theory]
    [InlineData(@"(?<=Card )204$", "Card 204")]
    [InlineData(@"^(Card )+(204)$", "Card 204")]
    [InlineData(@"^(\w+) \1$", "Echo Echo")]
    [InlineData(@"\A(?:Card ){1}(?:204){1}\z", "Card 204")]
    public void Full_original_regex_constructs_work(string regex, string name)
    {
        var col = Fixture() with { Cards = [new() { Quantity = 1, CardDetails = new() { Name = name } }] };
        Assert.Single(CollectionReadService.Project(col, new() { Query = regex, Regex = true, MatchWord = true }).Cards);
    }
    [Fact]
    public void Regex_has_no_200_character_limit_and_matches_all_pages()
    {
        var query = string.Join('|', Enumerable.Repeat("Does not exist", 30)) + "|(?<=Card )204$";
        Assert.Single(CollectionReadService.Project(Fixture(), new() { Query = query, Regex = true }).Cards);
    }
    [Fact]
    public void Original_token_case_regex_precedence_and_failure_behavior()
    {
        Assert.True(new OwnedNameMatcher("Aerie", false, true, false).Matches("Kangee, Aerie Keeper", ""));
        Assert.False(new OwnedNameMatcher("Aerie Keeper", false, true, false).Matches("Kangee, Aerie Keeper", ""));
        Assert.False(new OwnedNameMatcher("aerie", true, true, false).Matches("Kangee, Aerie Keeper", ""));
        Assert.True(new OwnedNameMatcher("A.*Keeper", false, true, true).Matches("Kangee, Aerie Keeper", ""));
        var invalid = CollectionReadService.Project(Fixture(), new() { Query = "[", Regex = true });
        Assert.Empty(invalid.Cards);
        Assert.Contains("invalid", invalid.PageData!.QueryError);
        var timer = Stopwatch.StartNew();
        var expensive = new OwnedNameMatcher("^(a+)+$", false, false, true);
        Assert.False(expensive.Matches(new string('a', 10000) + "!", ""));
        Assert.Contains("too long", expensive.Error);
        Assert.True(expensive.Matches("aaaa", ""));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
    }
    [Fact]
    public void Grouping_pages_whole_oracle_groups_not_partial_printings()
    {
        var col = Fixture(201);
        col = col with { Cards = [.. col.Cards, col.Cards[0] with { Id = Guid.NewGuid(), Quantity = 7 }] };
        var page = CollectionReadService.Project(col, new() { GroupSameCard = true });
        Assert.Equal(201, page.PageData!.Total);
        Assert.Equal(2, page.Cards.Count(c => c.OracleId == "oracle-0"));
    }
    [Fact]
    public async Task Queries_and_row_lookups_enforce_ownership_and_exclude_decks()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using var db = new MtgEngineDbContext(new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var col = new Collection("owner", "Owned");
        var deck = new Collection("owner", "Deck", isDeck: true);
        db.Collections.AddRange(col, deck);
        db.CollectionCards.AddRange(new CollectionCard(col.Id, "wanted", null, 3), new CollectionCard(col.Id, "other", null, 1));
        await db.SaveChangesAsync();
        var lookup = new Lookup();
        var service = new CollectionReadService(db, new CollectionService(db, lookup, new CardHistoryService(db)), lookup);
        Assert.Null(await service.PageAsync(col.Id, "stranger", new(), default));
        Assert.Null(await service.PageAsync(deck.Id, "owner", new(), default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.LookupAsync(col.Id, "stranger", ["wanted"], default));
        Assert.Equal("wanted", Assert.Single(await service.LookupAsync(col.Id, "owner", ["wanted"], default)).OracleId);
        Assert.Equal(2, (await service.PageAsync(col.Id, "owner", new(), default))!.PageData!.TotalRows);
    }
}
