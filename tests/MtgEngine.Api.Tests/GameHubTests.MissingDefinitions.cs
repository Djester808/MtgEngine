using MtgEngine.Api.Services;
using MtgEngine.Cards;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

public sealed partial class GameHubTests
{
    [Fact]
    public async Task Missing_definitions_refuse_start_and_reopen_the_table_for_retry()
    {
        var lobby = GameTableLobby.Create(Alice, "Alice", Guid.NewGuid(), 2, "casual", 20, true)
            .Invite(Alice, Bob, "Bob").BringDeck(Bob, Guid.NewGuid());
        var lobbies = new StartupLobbyStore(lobby);
        var logs = new MemoryLogStore();
        var pool = new FakePool();
        var games = new GameSessionService(logs, pool, new DeckGate(pool));
        var startup = new GameLobbyStartup(lobbies, new StartupDecks(), games);

        var error = await Assert.ThrowsAsync<InvalidRequestException>(() =>
            startup.StartAsync(lobby.Id, Alice, lobby.Revision));
        Assert.Contains("definition", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(lobbies.Current.Starting);
        Assert.Null(lobbies.Current.GameId);
        Assert.Empty(logs.Rows);

        foreach (var id in DeckIds())
            pool.With(id, "Available card", ImplementationStatus.Untranslated);
        var started = await startup.StartAsync(lobby.Id, Alice, lobbies.Current.Revision);
        Assert.Equal(lobby.Id, started.GameId);
        Assert.Single(logs.Rows);
        Assert.Empty((await games.FindAsync(started.GameId))!.EnforcedOracleIds);
    }
}
