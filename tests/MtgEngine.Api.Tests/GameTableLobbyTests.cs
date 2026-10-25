using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

public sealed class GameTableLobbyTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_player_brings_their_own_deck_before_the_host_can_start(int capacity)
    {
        var lobby = GameTableLobby.Create("host", "Host", Guid.NewGuid(), capacity, "commander", 40, false);
        Assert.Throws<InvalidOperationException>(() => lobby.BeginStart("host"));
        for (var seat = 1; seat < capacity; seat++)
        {
            lobby = lobby.Invite("host", $"user-{seat}", $"Player {seat}");
            Assert.Throws<InvalidOperationException>(() => lobby.BeginStart("host"));
            lobby = lobby.BringDeck($"user-{seat}", Guid.NewGuid());
        }
        Assert.Throws<InvalidOperationException>(() => lobby.BeginStart("user-1"));
        var starting = lobby.BeginStart("host");
        Assert.Throws<InvalidOperationException>(() => starting.BeginStart("host"));
        Assert.Throws<InvalidOperationException>(() => starting.BringDeck("host", Guid.NewGuid()));
        var game = Guid.NewGuid();
        var finished = starting.CompleteStart(game);
        Assert.Equal(game, finished.GameId);
        Assert.Throws<InvalidOperationException>(() => finished.Leave("host"));
        Assert.Throws<InvalidOperationException>(() => finished.CompleteStart(Guid.NewGuid()));
        Assert.Null(lobby.GameId);
        Assert.True(finished.Revision > lobby.Revision);
    }

    [Fact]
    public void Invites_are_host_only_bounded_and_distinct_and_deck_selection_requires_a_seat()
    {
        var lobby = GameTableLobby.Create("host", "Host", Guid.NewGuid(), 2, "commander", 40, false);
        Assert.Throws<InvalidOperationException>(() => lobby.Invite("stranger", "guest", "Guest"));
        Assert.Throws<InvalidOperationException>(() => lobby.Invite("host", "host", "Host"));
        Assert.Throws<InvalidOperationException>(() => lobby.BringDeck("stranger", Guid.NewGuid()));
        var invited = lobby.Invite("host", "guest", "Guest");
        Assert.Null(invited.Seats[1].DeckId);
        Assert.Throws<InvalidOperationException>(() => invited.Invite("host", "third", "Third"));
        Assert.Single(invited.Leave("guest").Seats);
        Assert.True(invited.Leave("host").Cancelled);
    }

    [Fact]
    public void Failed_start_can_reopen_without_losing_seat_consent()
    {
        var ready = GameTableLobby.Create("host", "Host", Guid.NewGuid(), 2, "commander", 40, false)
            .Invite("host", "guest", "Guest").BringDeck("guest", Guid.NewGuid());
        var retried = ready.BeginStart("host").ReopenAfterFailedStart();
        Assert.Equal(ready.Seats, retried.Seats);
        Assert.True(retried.BeginStart("host").Starting);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Table_capacity_is_two_to_four(int capacity) =>
        Assert.Throws<ArgumentException>(() => GameTableLobby.Create("host", "Host", Guid.NewGuid(), capacity, "commander", 40, false));
}
