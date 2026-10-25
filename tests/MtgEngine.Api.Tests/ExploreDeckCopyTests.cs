using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Services;
using MtgEngine.Api.Services.Community;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

public sealed class ExploreDeckCopyTests
{
    private const string Id = "24fc0e72-7851-4f37-a508-1ae2d4b8da71";
    private sealed class Lookup : StubScryfallService, IScryfallService
    {
        private readonly Dictionary<string, CardDefinition> _cards = [];
        public bool Missing { get; init; }
        public bool FailDuringWrite { get; init; }
        public Task<CardDefinition?> GetLocalByNameAsync(string name)
        {
            if (Missing)
                return Task.FromResult<CardDefinition?>(null);
            var card = new CardDefinition { OracleId = Guid.NewGuid().ToString(), Name = name };
            _cards[card.OracleId] = card;
            return Task.FromResult<CardDefinition?>(card);
        }
        public override Task<CardDefinition?> GetByOracleIdAsync(string oracleId)
        {
            if (oracleId != Id && FailDuringWrite)
                throw new InvalidOperationException("Simulated catalog failure during copy");
            return Task.FromResult<CardDefinition?>(_cards.GetValueOrDefault(oracleId)
                ?? new CardDefinition
                {
                    OracleId = Id,
                    Name = "Atraxa, Praetors' Voice",
                    CardTypes = CardType.Creature,
                    Supertypes = ["Legendary"],
                    Legalities = new Dictionary<string, string> { ["commander"] = "legal" }
                });
        }
    }
    private sealed class Cache : IAiCacheService
    {
        public Task<T> GetOrCreateAsync<T>(string kind, string modelVersion, IEnumerable<string?> keyParts, Func<Task<T>> factory, TimeSpan? ttl = null) => factory();
    }
    private sealed class Upstream : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://fixture.test/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                EdhrecFixtures.Text(request.RequestUri!.AbsolutePath.Contains("average-decks", StringComparison.Ordinal) ? EdhrecFixtures.AverageDeck : EdhrecFixtures.Commander))
            });
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Whole_deck_copy_preserves_quantities_or_saves_nothing(bool missing, bool failDuringWrite)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using var db = new MtgEngineDbContext(new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var lookup = new Lookup { Missing = missing, FailDuringWrite = failDuringWrite };
        using var upstream = new Upstream();
        var service = new ExploreDeckCopyService(new ExploreService(lookup, upstream, new Cache()), new CollectionService(db, lookup, new CardHistoryService(db)), db);
        if (missing || failDuringWrite)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => service.Copy("owner", Id, "", default));
            db.ChangeTracker.Clear();
            Assert.False(await db.Collections.AnyAsync());
            Assert.False(await db.CollectionCards.AnyAsync());
        }
        else
        {
            var result = await service.Copy("owner", Id, "", default);
            using var document = JsonDocument.Parse(EdhrecFixtures.Text(EdhrecFixtures.AverageDeck));
            var original = EdhrecAverageDeckParser.Parse(document);
            Assert.Equal(original.Sections.Sum(s => s.Cards.Sum(c => c.Q)) + original.Commanders.Length, result.Cards.Sum(c => c.Quantity));
            Assert.Equal(Id, result.CommanderOracleId);
            Assert.Equal("commander", result.Format);
            Assert.Single(await db.Collections.ToListAsync());
        }
    }
}
