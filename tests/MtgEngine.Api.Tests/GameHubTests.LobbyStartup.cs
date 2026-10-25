using MtgEngine.Api.Services;
using MtgEngine.Cards;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

public sealed partial class GameHubTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Ready_tables_start_once_and_recover_after_the_game_was_saved(int count)
    {
        var lobby = ReadyLobby(count);
        var lobbies = new StartupLobbyStore(lobby);
        var logs = new MemoryLogStore();
        var pool = Deck(ImplementationStatus.Verified);
        var games = new GameSessionService(logs, pool, new DeckGate(pool));
        var startup = new GameLobbyStartup(lobbies, new StartupDecks(), games);
        // Simulate interruption after claiming startup and saving the game, before linking it.
        var claimed = lobby.BeginStart(Alice);
        Assert.True(await lobbies.TryUpdateAsync(claimed, lobby.Revision));
        await games.CreateForLobbyAsync(lobby.Id, new NewGameRequest(
            lobby.Seats.Select(seat => new NewGameSeat(seat.UserId, seat.Name, [.. DeckIds()], [])).ToArray(),
            1, false, "commander", 40));
        var restarted = new GameSessionService(logs, pool, new DeckGate(pool));
        startup = new GameLobbyStartup(lobbies, new StartupDecks(), restarted);
        var result = await startup.StartAsync(lobby.Id, Alice, lobby.Revision);
        Assert.Equal(lobby.Id, result.GameId);
        Assert.Equal(result, await startup.StartAsync(lobby.Id, Alice, lobby.Revision));
        Assert.Single(logs.Rows);
        Assert.Equal(lobby.Seats.Select(seat => seat.UserId), (await restarted.FindAsync(result.GameId))!.Seats);
        Assert.False(lobbies.Current.Starting);
        Assert.Equal(result.GameId, lobbies.Current.GameId);
    }

    [Fact]
    public async Task Startup_requires_host_consent_and_current_revision_and_reopens_on_invalid_deck()
    {
        var lobby = ReadyLobby(2);
        var lobbies = new StartupLobbyStore(lobby);
        var logs = new MemoryLogStore();
        var pool = Deck(ImplementationStatus.Verified);
        var games = new GameSessionService(logs, pool, new DeckGate(pool));
        var decks = new StartupDecks { Refuse = true };
        var startup = new GameLobbyStartup(lobbies, decks, games);
        await Assert.ThrowsAsync<InvalidResourceStateException>(() => startup.StartAsync(lobby.Id, Bob, lobby.Revision));
        await Assert.ThrowsAsync<InvalidResourceStateException>(() => startup.StartAsync(lobby.Id, Alice, lobby.Revision - 1));
        await Assert.ThrowsAsync<InvalidRequestException>(() => startup.StartAsync(lobby.Id, Alice, lobby.Revision));
        Assert.False(lobbies.Current.Starting);
        Assert.Equal(lobby.Seats, lobbies.Current.Seats);
        Assert.Empty(logs.Rows);
        decks.Refuse = false;
        var started = await startup.StartAsync(lobby.Id, Alice, lobbies.Current.Revision);
        Assert.Equal(lobby.Id, started.GameId);
        Assert.Single(logs.Rows);
    }

    private static GameTableLobby ReadyLobby(int count)
    {
        var lobby = GameTableLobby.Create(Alice, "Alice", Guid.NewGuid(), count, "commander", 40, false);
        foreach (var account in new[] { Bob, "third", "fourth" }.Take(count - 1))
            lobby = lobby.Invite(Alice, account, account).BringDeck(account, Guid.NewGuid());
        return lobby;
    }

    private sealed class StartupDecks : IGameLobbyDecks
    {
        public bool Refuse { get; set; }
        public Task<IReadOnlyList<LobbyDeckChoice>> ListAsync(string account, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LobbyDeckChoice>>([]);
        public Task<LobbyDeck> LoadAsync(Guid deckId, string account, string format, bool allowHonourSystem, CancellationToken ct = default) =>
            Refuse ? throw new InvalidRequestException("Deck unavailable.") : Task.FromResult(new LobbyDeck(deckId, account, [.. DeckIds()], []));
    }

    private sealed class StartupLobbyStore(GameTableLobby initial) : IGameLobbyStore
    {
        public GameTableLobby Current { get; private set; } = initial;
        public Task CreateAsync(GameTableLobby lobby, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GameTableLobby?> FindAsync(Guid id, string account, CancellationToken ct = default) =>
            Task.FromResult(id == Current.Id && Current.Seats.Any(seat => seat.UserId == account) ? Current : null);
        public Task<IReadOnlyList<GameTableLobby>> ListAsync(string account, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameTableLobby>>([Current]);
        public Task<bool> TryUpdateAsync(GameTableLobby next, long expectedRevision, CancellationToken ct = default)
        {
            if (Current.Revision != expectedRevision)
                return Task.FromResult(false);
            Current = next;
            return Task.FromResult(true);
        }
    }
}
