using Microsoft.Extensions.DependencyInjection;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

public sealed partial class GameLobbyStoreTests
{
    [Fact]
    public async Task Player_search_is_bounded_and_excludes_self_deleted_and_nonmatching_accounts()
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();
        var host = new User { Username = "PlayerHost", Email = "host@example.test" };
        var deleted = new User { Username = "PlayerDeleted", Email = "deleted@example.test", DeletedAt = DateTime.UtcNow };
        db.Users.AddRange(host, deleted, new User { Username = "Different", Email = "different@example.test" });
        for (var index = 0; index < 25; index++)
            db.Users.Add(new User { Username = $"Player{index:D2}", Email = $"p{index}@example.test" });
        await db.SaveChangesAsync();
        var service = new GameLobbyService(store, new LobbyTestDecks(), db);
        var actor = host.Id.ToString();
        Assert.Empty(await service.PlayersAsync(actor, " "));
        Assert.Empty(await service.PlayersAsync(actor, "P"));
        var result = await service.PlayersAsync(actor, "  Player  ");
        Assert.Equal(20, result.Count);
        Assert.Equal("Player00", result[0].Username);
        Assert.Equal("Player19", result[^1].Username);
        Assert.DoesNotContain(result, player => player.UserId == host.Id || player.UserId == deleted.Id);
        Assert.Single(await service.PlayersAsync(actor, "Player24"));
        await Assert.ThrowsAsync<InvalidRequestException>(() => service.PlayersAsync(actor, new string('a', 65)));
        await Assert.ThrowsAsync<InvalidRequestException>(() => service.PlayersAsync("invalid", "Player"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Lobby_service_binds_each_deck_to_its_actor_and_redacts_other_players_deck_ids(int capacity)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();
        var accounts = Enumerable.Range(0, capacity).Select(_ => Guid.NewGuid()).ToArray();
        var decks = new LobbyTestDecks();
        foreach (var id in accounts)
        {
            db.Users.Add(new User { Id = id, Username = $"user-{id:N}", Email = $"{id:N}@example.test" });
            decks.Owners.Add(Guid.NewGuid(), id.ToString());
        }
        await db.SaveChangesAsync();
        var selected = decks.Owners.Keys.ToArray();
        var service = new GameLobbyService(store, decks, db);
        var host = accounts[0].ToString();
        var lobby = await service.CreateAsync(host,
            new CreateGameLobbyRequest(selected[0], capacity, "commander", 40, false, accounts.Skip(1).ToArray()));
        Assert.Equal(capacity, lobby.Seats.Count);
        Assert.True(lobby.Seats[0].Ready);
        Assert.True(lobby.IsHost);
        Assert.All(lobby.Seats.Skip(1), seat => Assert.False(seat.Ready));
        for (var index = 1; index < capacity; index++)
        {
            var actor = accounts[index].ToString();
            var invited = await service.GetAsync(lobby.Id, actor);
            Assert.False(invited.IsHost);
            Assert.Null(invited.Seats[0].DeckId);
            lobby = await service.BringDeckAsync(lobby.Id, actor, new BringLobbyDeckRequest(selected[index], invited.Revision));
            Assert.Equal(selected[index], lobby.Seats[index].DeckId);
            Assert.All(lobby.Seats.Where(seat => seat.UserId != actor), seat => Assert.Null(seat.DeckId));
        }
        Assert.All(lobby.Seats, seat => Assert.True(seat.Ready));
        Assert.Null((await service.GetAsync(lobby.Id, host)).Seats[1].DeckId);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => service.GetAsync(lobby.Id, Guid.NewGuid().ToString()));
        Assert.Empty(await service.ListAsync(Guid.NewGuid().ToString()));
        await Assert.ThrowsAsync<InvalidResourceStateException>(() => service.LeaveAsync(lobby.Id, host, lobby.Revision - 1));
        var cancelled = await service.LeaveAsync(lobby.Id, host, lobby.Revision);
        Assert.True(cancelled.Cancelled);
    }

    [Fact]
    public async Task An_invited_account_cannot_select_the_hosts_private_deck()
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();
        var host = new User { Username = "Host", Email = "host@example.test" };
        var guest = new User { Username = "Guest", Email = "guest@example.test" };
        db.Users.AddRange(host, guest);
        await db.SaveChangesAsync();
        var decks = new LobbyTestDecks();
        var privateDeck = Guid.NewGuid();
        decks.Owners.Add(privateDeck, host.Id.ToString());
        var service = new GameLobbyService(store, decks, db);
        var lobby = await service.CreateAsync(host.Id.ToString(), new CreateGameLobbyRequest(privateDeck, 2, "commander", 40, false, [guest.Id]));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => service.BringDeckAsync(lobby.Id, guest.Id.ToString(), new BringLobbyDeckRequest(privateDeck, lobby.Revision)));
        Assert.False((await service.GetAsync(lobby.Id, host.Id.ToString())).Seats[1].Ready);
        await Assert.ThrowsAsync<InvalidResourceStateException>(() => service.CreateAsync(host.Id.ToString(), new CreateGameLobbyRequest(privateDeck, 2, "commander", 40, false, [host.Id])));
        Assert.Single(await service.ListAsync(host.Id.ToString()));
    }

    private sealed class LobbyTestDecks : IGameLobbyDecks
    {
        public Dictionary<Guid, string> Owners { get; } = [];
        public Task<IReadOnlyList<LobbyDeckChoice>> ListAsync(string account, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LobbyDeckChoice>>([]);
        public Task<LobbyDeck> LoadAsync(Guid id, string account, string format, bool allowHonourSystem, CancellationToken ct = default)
        {
            if (!Owners.TryGetValue(id, out var owner) || owner != account)
                throw new ResourceNotFoundException("Deck not found.");
            return Task.FromResult(new LobbyDeck(id, "Deck", ["land"], ["leader"]));
        }
    }
}
