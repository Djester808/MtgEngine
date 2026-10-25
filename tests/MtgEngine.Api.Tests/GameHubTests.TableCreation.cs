using MtgEngine.Api.Services;
using MtgEngine.Cards;

namespace MtgEngine.Api.Tests;

public sealed partial class GameHubTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public async Task Table_creation_refuses_sizes_outside_two_to_four_before_persisting(int count)
    {
        var store = new MemoryLogStore();
        var pool = Deck(ImplementationStatus.Verified);
        var sessions = new GameSessionService(store, pool, new DeckGate(pool));
        var seats = Enumerable.Range(0, count).Select(index => new NewGameSeat($"user-{index}", $"Player {index}", [.. DeckIds()], [])).ToArray();
        await Assert.ThrowsAsync<InvalidRequestException>(() => sessions.CreateAsync(new NewGameRequest(seats, 1, false, "commander", 40)));
        Assert.Empty(store.Rows);
    }

    [Theory]
    [InlineData("user-alice", "Alice")]
    [InlineData("", "Bob")]
    [InlineData(" ", "Bob")]
    [InlineData("user-bob", " ")]
    public async Task Table_creation_requires_distinct_identified_seats(string otherAccount, string otherName)
    {
        var store = new MemoryLogStore();
        var pool = Deck(ImplementationStatus.Verified);
        var sessions = new GameSessionService(store, pool, new DeckGate(pool));
        await Assert.ThrowsAsync<InvalidRequestException>(() => sessions.CreateAsync(new NewGameRequest(
            [new NewGameSeat(Alice, "Alice", [.. DeckIds()], []), new NewGameSeat(otherAccount, otherName, [.. DeckIds()], [])],
            1, false, "commander", 40)));
        Assert.Empty(store.Rows);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Every_supported_table_size_keeps_its_account_to_seat_mapping_after_restart(int count)
    {
        var store = new MemoryLogStore();
        var pool = Deck(ImplementationStatus.Verified);
        var sessions = new GameSessionService(store, pool, new DeckGate(pool));
        var seats = Enumerable.Range(0, count).Select(index => new NewGameSeat($"user-{index}", $"Player {index}", [.. DeckIds()], [])).ToArray();
        var game = await sessions.CreateAsync(new NewGameRequest(seats, 1, false, "commander", 40));
        var restarted = await new GameSessionService(store, pool, new DeckGate(pool)).FindAsync(game.Id);
        Assert.NotNull(restarted);
        Assert.Equal(count, restarted.State.Players.Count);
        for (var index = 0; index < count; index++)
        {
            Assert.Equal(index, restarted.SeatOf(seats[index].UserId)!.Value.Value);
            var view = restarted.ProjectFor(new Rules.State.PlayerId(index)).View;
            Assert.NotNull(view);
            Assert.Equal(count, view.Players.Count);
        }
        Assert.Null(restarted.SeatOf("uninvited-account"));
    }
}
