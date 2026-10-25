using MtgEngine.Api.Services;
using MtgEngine.Cards;

namespace MtgEngine.Api.Tests;

public sealed partial class GameHubTests
{
    [Fact]
    public async Task A_lobby_creation_retry_and_restart_return_the_same_game_without_replacing_its_log()
    {
        var store = new MemoryLogStore();
        var pool = Deck(ImplementationStatus.Verified);
        var sessions = new GameSessionService(store, pool, new DeckGate(pool));
        var request = new NewGameRequest(
            [new NewGameSeat(Alice, "Alice", [.. DeckIds()], []), new NewGameSeat(Bob, "Bob", [.. DeckIds()], [])],
            1, false, "commander", 40);
        var lobbyId = Guid.NewGuid();
        var original = await sessions.CreateForLobbyAsync(lobbyId, request);
        var saved = store.Rows[lobbyId].LogJson;
        Assert.Equal(lobbyId, original.Id);
        Assert.Same(original, await sessions.CreateForLobbyAsync(lobbyId, request with { Seed = 999 }));
        var restarted = new GameSessionService(store, pool, new DeckGate(pool));
        var recovered = await restarted.CreateForLobbyAsync(lobbyId, request);
        Assert.Equal(original.Id, recovered.Id);
        Assert.Equal(saved, store.Rows[lobbyId].LogJson);
        Assert.Single(store.Rows);
        await Assert.ThrowsAsync<InvalidResourceStateException>(() => restarted.CreateForLobbyAsync(lobbyId,
            request with { Seats = request.Seats.Reverse().ToArray() }));
    }
}
