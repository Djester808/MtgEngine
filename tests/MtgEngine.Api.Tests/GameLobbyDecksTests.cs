using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgEngine.Api.Data;
using MtgEngine.Api.Services;
using MtgEngine.Cards;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.State;

namespace MtgEngine.Api.Tests;

[Trait("Scope", "Deck")]
public sealed class GameLobbyDecksTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly MtgEngineDbContext db;
    private readonly GameLobbyDecks reader;
    private readonly Collection deck = new("owner", "My deck", isDeck: true) { Format = "commander", CommanderOracleId = "leader" };

    public GameLobbyDecksTests()
    {
        connection.Open();
        db = new MtgEngineDbContext(new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Collections.Add(deck);
        db.CollectionCards.AddRange(new CollectionCard(deck.Id, "leader"),
            new CollectionCard(deck.Id, "land", quantity: 97, quantityFoil: 1) { QuantityEtched = 1 },
            new CollectionCard(deck.Id, "unknown-side", board: "side"), new CollectionCard(deck.Id, "unknown-maybe", board: "maybe"));
        db.SaveChanges();
        reader = new GameLobbyDecks(db, new DeckGate(new Pool()), new CommanderDeckAdmission(new Lookup()));
    }

    [Fact]
    public async Task Only_owned_decks_are_listed_or_loaded()
    {
        Assert.Empty(await reader.ListAsync("stranger"));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => reader.LoadAsync(deck.Id, "stranger", "commander", false));
        var offered = Assert.Single(await reader.ListAsync("owner"));
        Assert.Equal(100, offered.CardCount);
        deck.IsDeck = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => reader.LoadAsync(deck.Id, "owner", "commander", false));
    }

    [Fact]
    public async Task Main_deck_finishes_expand_to_cards_and_one_commander_moves_out_of_the_library()
    {
        var loaded = await reader.LoadAsync(deck.Id, "owner", "commander", false);
        Assert.Equal(new[] { "leader" }, loaded.Command.Select(card => card.OracleId));
        Assert.Equal(99, loaded.Library.Count);
        Assert.All(loaded.Library, card => Assert.Equal("land", card.OracleId));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    public async Task Admitted_commander_deck_starts_and_reloads_with_commanders_outside_the_library(int seats, bool paired)
    {
        if (paired)
        {
            deck.SecondaryCommanderOracleId = "partner";
            db.CollectionCards.Add(new CollectionCard(deck.Id, "partner"));
            db.CollectionCards.Single(card => card.OracleId == "land").Quantity--;
            await db.SaveChangesAsync();
        }
        var loaded = await reader.LoadAsync(deck.Id, "owner", "commander", false);
        using var services = new ServiceCollection()
            .AddDbContext<MtgEngineDbContext>(options => options.UseSqlite(connection))
            .BuildServiceProvider();
        var store = new GameLogStore(services.GetRequiredService<IServiceScopeFactory>());
        var pool = new Pool();
        var sessions = new GameSessionService(store, pool, new DeckGate(pool));
        var created = await sessions.CreateAsync(new NewGameRequest(
            Enumerable.Range(0, seats).Select(seat => new NewGameSeat($"seat-{seat}", $"Seat {seat}",
                loaded.Library, loaded.Command)).ToArray(), 42, false, "commander", 40));
        var restarted = new GameSessionService(store, pool, new DeckGate(pool));
        var restored = Assert.IsType<GameSession>(await restarted.FindAsync(created.Id));
        foreach (var state in new[] { created.State, restored.State })
        {
            Assert.NotNull(state.Pending);
            for (var seat = 0; seat < seats; seat++)
            {
                var player = new PlayerId(seat);
                var command = state.In(ZoneKey.Shared(Zone.Command))
                    .Where(id => state.Objects[id].Owner == player).ToList();
                var library = state.In(ZoneKey.Of(Zone.Library, player));
                var hand = state.In(ZoneKey.Of(Zone.Hand, player));
                Assert.Equal(paired ? 2 : 1, command.Count);
                Assert.Equal(paired ? 91 : 92, library.Count);
                Assert.Equal(7, hand.Count);
                Assert.Equal(100, command.Count + library.Count + hand.Count);
                Assert.All(command, id => Assert.True(state.Objects[id].IsCommander));
                Assert.All(library.Concat(hand), id => Assert.False(state.Objects[id].IsCommander));
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Startup_revalidates_saved_decks_and_reopens_without_a_game_before_a_corrected_retry(int seats)
    {
        var lobby = GameTableLobby.Create("owner", "Owner", deck.Id, seats, "commander", 40, false);
        CollectionCard? changedLand = null;
        for (var seat = 1; seat < seats; seat++)
        {
            var actor = $"guest-{seat}";
            var guestDeck = new Collection(actor, "Guest deck", isDeck: true)
            { Format = "commander", CommanderOracleId = "leader" };
            changedLand = new CollectionCard(guestDeck.Id, "land", quantity: 99);
            db.Collections.Add(guestDeck);
            db.CollectionCards.AddRange(new CollectionCard(guestDeck.Id, "leader"), changedLand);
            lobby = lobby.Invite("owner", actor, actor).BringDeck(actor, guestDeck.Id);
        }
        await db.SaveChangesAsync();
        using var services = new ServiceCollection()
            .AddDbContext<MtgEngineDbContext>(options => options.UseSqlite(connection))
            .BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var lobbies = new GameLobbyStore(scopes);
        await lobbies.CreateAsync(lobby);
        var logs = new GameLogStore(scopes);
        var pool = new Pool();
        var sessions = new GameSessionService(logs, pool, new DeckGate(pool));
        var startup = new GameLobbyStartup(lobbies, reader, sessions);
        changedLand!.Quantity = 98;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidRequestException>(() => startup.StartAsync(lobby.Id, "owner", lobby.Revision));
        Assert.Null(await logs.LoadAsync(lobby.Id));
        Assert.Null(await sessions.FindAsync(lobby.Id));
        var reopened = Assert.IsType<GameTableLobby>(await lobbies.FindAsync(lobby.Id, "owner"));
        Assert.False(reopened.Starting);
        Assert.Null(reopened.GameId);
        Assert.Equal(seats, reopened.Seats.Count);

        changedLand.Quantity = 99;
        await db.SaveChangesAsync();
        var started = await startup.StartAsync(lobby.Id, "owner", reopened.Revision);
        Assert.Equal(lobby.Id, started.GameId);
        var restored = Assert.IsType<GameSession>(await new GameSessionService(logs, pool, new DeckGate(pool)).FindAsync(started.GameId));
        Assert.Equal(lobby.Seats.Select(seat => seat.UserId), restored.Seats);
        Assert.NotNull(restored.State.Pending);
        for (var seat = 0; seat < seats; seat++)
        {
            var player = new PlayerId(seat);
            Assert.Equal(92, restored.State.In(ZoneKey.Of(Zone.Library, player)).Count);
            Assert.Equal(7, restored.State.In(ZoneKey.Of(Zone.Hand, player)).Count);
            var commander = Assert.Single(restored.State.In(ZoneKey.Shared(Zone.Command)),
                id => restored.State.Objects[id].Owner == player);
            Assert.True(restored.State.Objects[commander].IsCommander);
        }
        var log = (await logs.LoadAsync(started.GameId))!.LogJson;
        Assert.Equal(started.GameId, (await startup.StartAsync(lobby.Id, "owner", started.Revision)).GameId);
        Assert.Equal(log, (await logs.LoadAsync(started.GameId))!.LogJson);
    }

    [Fact]
    public async Task Wrong_formats_and_missing_commanders_are_refused()
    {
        await Assert.ThrowsAsync<InvalidRequestException>(() => reader.LoadAsync(deck.Id, "owner", "modern", false));
        deck.CommanderOracleId = "absent";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidRequestException>(() => reader.LoadAsync(deck.Id, "owner", "commander", false));
    }

    [Fact]
    public async Task Startup_deck_retains_selected_printings_and_each_finish()
    {
        db.CollectionCards.Single(card => card.OracleId == "leader").ScryfallId = "commander-printing";
        db.CollectionCards.Single(card => card.OracleId == "land").ScryfallId = "land-printing";
        await db.SaveChangesAsync();
        var loaded = await reader.LoadAsync(deck.Id, "owner", "commander", false);
        Assert.Equal("commander-printing", Assert.Single(loaded.Command).PrintingId);
        Assert.All(loaded.Library, card => Assert.Equal("land-printing", card.PrintingId));
        Assert.Equal(97, loaded.Library.Count(card => card.Finish == CardFinish.Nonfoil));
        Assert.Single(loaded.Library, card => card.Finish == CardFinish.Foil);
        Assert.Single(loaded.Library, card => card.Finish == CardFinish.Etched);
    }

    [Fact]
    public async Task Unverified_main_cards_need_the_tables_honour_setting()
    {
        db.CollectionCards.Add(new CollectionCard(deck.Id, "unknown-main"));
        db.CollectionCards.Single(card => card.OracleId == "land").Quantity--;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidRequestException>(() => reader.LoadAsync(deck.Id, "owner", "commander", false));
        Assert.Contains((await reader.LoadAsync(deck.Id, "owner", "commander", true)).Library, card => card.OracleId == "unknown-main");
    }

    [Fact]
    public async Task Honour_system_does_not_bypass_commander_deck_size()
    {
        db.CollectionCards.Single(card => card.OracleId == "land").Quantity--;
        await db.SaveChangesAsync();
        var refused = await Assert.ThrowsAsync<InvalidRequestException>(() => reader.LoadAsync(deck.Id, "owner", "commander", true));
        Assert.Contains("100", refused.Message);
    }

    [Fact]
    public async Task Both_saved_commanders_start_outside_the_ninety_eight_card_library()
    {
        deck.SecondaryCommanderOracleId = "partner";
        db.CollectionCards.Add(new CollectionCard(deck.Id, "partner"));
        db.CollectionCards.Single(card => card.OracleId == "land").Quantity--;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var loaded = await reader.LoadAsync(deck.Id, "owner", "commander", false);
        Assert.Equal(new[] { "leader", "partner" }, loaded.Command.Select(card => card.OracleId));
        Assert.Equal(98, loaded.Library.Count);
        Assert.All(loaded.Library, card => Assert.Equal("land", card.OracleId));
    }

    [Fact]
    public async Task A_second_commander_absent_from_the_main_deck_is_refused()
    {
        deck.SecondaryCommanderOracleId = "absent";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidRequestException>(() => reader.LoadAsync(deck.Id, "owner", "commander", false));
    }

    [Fact]
    public async Task Deck_requests_round_trip_and_clear_the_saved_second_commander()
    {
        var service = new CollectionService(db, new Lookup(), new CardHistoryService(db));
        var created = await service.CreateDeckAsync("owner", new MtgEngine.Api.Dtos.CreateDeckRequest(
            "Pair", Format: "commander", CommanderOracleId: "leader", SecondaryCommanderOracleId: "partner"));
        Assert.Equal("partner", created.SecondaryCommanderOracleId);
        db.ChangeTracker.Clear();
        Assert.Equal("partner", (await service.GetDeckAsync(created.Id, "owner"))!.SecondaryCommanderOracleId);
        var updated = await service.UpdateDeckAsync(created.Id, "owner", new MtgEngine.Api.Dtos.UpdateDeckRequest(
            "Single", Format: "commander", CommanderOracleId: "leader", SecondaryCommanderOracleId: null));
        Assert.Null(updated.SecondaryCommanderOracleId);
        db.ChangeTracker.Clear();
        Assert.Null((await service.GetDeckAsync(created.Id, "owner"))!.SecondaryCommanderOracleId);
    }

    private sealed class Pool : ICardFilePool
    {
        public int Count => 2;
        public CardStatus StatusOf(string id) => new(id, id, id.StartsWith("unknown", StringComparison.Ordinal) ? ImplementationStatus.Translated : ImplementationStatus.Verified);
        public MtgEngine.Cards.CardDefinition? ByOracleId(string id) => new(id, id, Layout.Normal,
            [new Face(id, FaceRole.Main, id == "land"
                ? new TypeLine(CardType.Land, Supertype.Basic, [])
                : new TypeLine(CardType.Creature, Supertype.Legendary, []))]);
        public MtgEngine.Cards.CardDefinition? ByName(string name) => null;
    }

    private sealed class Lookup : StubScryfallService
    {
        public override Task<MtgEngine.Domain.Models.CardDefinition?> GetByOracleIdAsync(string oracleId) =>
            Task.FromResult<MtgEngine.Domain.Models.CardDefinition?>(new()
            {
                OracleId = oracleId,
                Name = oracleId,
                CardTypes = oracleId == "land" ? MtgEngine.Domain.Enums.CardType.Land : MtgEngine.Domain.Enums.CardType.Creature,
                Supertypes = oracleId == "land" ? ["Basic"] : ["Legendary"],
                OracleText = oracleId is "leader" or "partner" ? "Partner" : "",
                Legalities = new Dictionary<string, string> { ["commander"] = "legal" },
            });
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }
}
