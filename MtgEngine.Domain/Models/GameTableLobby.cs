using System.Collections.Immutable;

namespace MtgEngine.Domain.Models;

public sealed record GameTableSeat(string UserId, string Name, Guid? DeckId);

/// <summary>Immutable lobby transitions. Callers supply the authenticated actor, never a seat chosen by a client.</summary>
public sealed record GameTableLobby
{
    public required Guid Id { get; init; }

    public required string HostUserId { get; init; }

    public required int Capacity { get; init; }

    public required string Format { get; init; }

    public required int StartingLife { get; init; }

    public bool AllowHonourSystem { get; init; }

    public ImmutableList<GameTableSeat> Seats { get; init; } = [];

    public long Revision { get; init; }

    public bool Starting { get; init; }

    public bool Cancelled { get; init; }

    public Guid? GameId { get; init; }

    public static GameTableLobby Create(string host, string name, Guid deckId, int capacity, string format, int startingLife, bool allowHonourSystem)
    {
        Identity(host, name);
        if (capacity is < 2 or > 4 || deckId == Guid.Empty || string.IsNullOrWhiteSpace(format) || startingLife < 1)
            throw new ArgumentException("Choose a deck, two to four players, a format and a positive starting life total.");
        return new GameTableLobby
        {
            Id = Guid.NewGuid(),
            HostUserId = host,
            Capacity = capacity,
            Format = format,
            StartingLife = startingLife,
            AllowHonourSystem = allowHonourSystem,
            Seats = [new GameTableSeat(host, name, deckId)],
        };
    }

    public GameTableLobby Invite(string actor, string account, string name)
    {
        Host(actor);
        Mutable();
        Identity(account, name);
        if (Seats.Count >= Capacity || Seats.Any(seat => seat.UserId == account))
            throw new InvalidOperationException("That seat is full or the account is already at this table.");
        return this with { Seats = Seats.Add(new GameTableSeat(account, name, null)), Revision = Revision + 1 };
    }

    /// <summary>The service must verify ownership of the selected deck before applying this transition.</summary>
    public GameTableLobby BringDeck(string actor, Guid deckId)
    {
        Mutable();
        var index = Seats.FindIndex(seat => seat.UserId == actor);
        if (index < 0 || deckId == Guid.Empty)
            throw new InvalidOperationException("Choose your own deck for a table you were invited to.");
        return this with { Seats = Seats.SetItem(index, Seats[index] with { DeckId = deckId }), Revision = Revision + 1 };
    }

    public GameTableLobby BeginStart(string actor)
    {
        Host(actor);
        Mutable();
        if (Seats.Count != Capacity || Seats.Any(seat => seat.DeckId is null))
            throw new InvalidOperationException("Every invited player must bring a deck before starting.");
        return this with { Starting = true, Revision = Revision + 1 };
    }

    public GameTableLobby CompleteStart(Guid gameId)
    {
        if (!Starting || GameId is not null || gameId == Guid.Empty)
            throw new InvalidOperationException("This table is not awaiting game creation.");
        return this with { Starting = false, GameId = gameId, Revision = Revision + 1 };
    }

    public GameTableLobby ReopenAfterFailedStart()
    {
        if (!Starting || GameId is not null)
            throw new InvalidOperationException("This table has no failed start to retry.");
        return this with { Starting = false, Revision = Revision + 1 };
    }

    public GameTableLobby Leave(string actor)
    {
        Mutable();
        var index = Seats.FindIndex(seat => seat.UserId == actor);
        if (index < 0)
            throw new InvalidOperationException("That account is not seated at this table.");
        return actor == HostUserId
            ? this with { Cancelled = true, Revision = Revision + 1 }
            : this with { Seats = Seats.RemoveAt(index), Revision = Revision + 1 };
    }

    private void Host(string actor)
    {
        if (actor != HostUserId)
            throw new InvalidOperationException("Only the host may manage this table.");
    }

    private void Mutable()
    {
        if (Cancelled || Starting || GameId is not null)
            throw new InvalidOperationException("This table is closed to changes.");
    }

    private static void Identity(string account, string name)
    {
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A seat needs an account and a player name.");
    }
}
