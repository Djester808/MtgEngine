using System.Security.Cryptography;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public sealed class GameLobbyStartup(IGameLobbyStore store, IGameLobbyDecks decks, GameSessionService games)
{
    public async Task<GameLobbyStartDto> StartAsync(Guid id, string actor, long revision, CancellationToken ct = default)
    {
        var lobby = await store.FindAsync(id, actor, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("Table not found.");
        if (lobby.HostUserId != actor || lobby.Cancelled)
            throw new InvalidResourceStateException("Only the host may start an open table.");
        if (lobby.GameId is { } started)
            return new GameLobbyStartDto(started, lobby.Revision);
        if (!lobby.Starting)
        {
            if (lobby.Revision != revision)
                throw new InvalidResourceStateException("The table changed. Refresh it before starting.");
            GameTableLobby claimed;
            try
            { claimed = lobby.BeginStart(actor); }
            catch (InvalidOperationException) { throw new InvalidResourceStateException("Every seat must bring a deck before starting."); }
            if (!await store.TryUpdateAsync(claimed, lobby.Revision, ct).ConfigureAwait(false))
                throw new InvalidResourceStateException("The table changed. Refresh it before starting.");
            lobby = claimed;
        }

        var game = await games.FindAsync(id, ct).ConfigureAwait(false);
        if (game is null)
        {
            try
            {
                var seats = new List<NewGameSeat>();
                foreach (var seat in lobby.Seats)
                {
                    var deck = await decks.LoadAsync(seat.DeckId!.Value, seat.UserId, lobby.Format, lobby.AllowHonourSystem, ct).ConfigureAwait(false);
                    seats.Add(new NewGameSeat(seat.UserId, seat.Name, deck.Library, deck.Command));
                }
                game = await games.CreateForLobbyAsync(id, new NewGameRequest(seats,
                    BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(sizeof(ulong))),
                    lobby.AllowHonourSystem, lobby.Format, lobby.StartingLife), ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidRequestException or ResourceNotFoundException)
            {
                await store.TryUpdateAsync(lobby.ReopenAfterFailedStart(), lobby.Revision, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        if (!game.Seats.SequenceEqual(lobby.Seats.Select(seat => seat.UserId)))
            throw new InvalidResourceStateException("The saved game does not match this table's seats.");
        var finished = lobby.CompleteStart(game.Id);
        if (await store.TryUpdateAsync(finished, lobby.Revision, ct).ConfigureAwait(false))
            return new GameLobbyStartDto(game.Id, finished.Revision);
        var recovered = await store.FindAsync(id, actor, ct).ConfigureAwait(false);
        if (recovered?.GameId == game.Id)
            return new GameLobbyStartDto(game.Id, recovered.Revision);
        throw new InvalidResourceStateException("Game saved. Refresh the table and retry starting to reconnect it.");
    }
}
