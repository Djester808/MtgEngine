using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public sealed class GameLobbyService(IGameLobbyStore store, IGameLobbyDecks decks, MtgEngineDbContext db)
{
    public async Task<IReadOnlyList<GameLobbyPlayerDto>> PlayersAsync(string actor, string? search, CancellationToken ct = default)
    {
        await AccountAsync(actor, ct).ConfigureAwait(false);
        var prefix = search?.Trim() ?? string.Empty;
        if (prefix.Length < 2)
            return [];
        if (prefix.Length > 64)
            throw new InvalidRequestException("Search using up to 64 characters.");
        var ownId = Guid.Parse(actor);
        return await db.Users.AsNoTracking()
            .Where(user => user.Id != ownId && user.Username.StartsWith(prefix))
            .OrderBy(user => user.Username).ThenBy(user => user.Id).Take(20)
            .Select(user => new GameLobbyPlayerDto(user.Id, user.Username))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<GameLobbyDto> CreateAsync(string actor, CreateGameLobbyRequest request, CancellationToken ct = default)
    {
        if (request.Invitees is null || request.Capacity is < 2 or > 4 || request.Invitees.Count != request.Capacity - 1)
            throw new InvalidRequestException("Choose one to three players to fill the table.");
        var host = await AccountAsync(actor, ct).ConfigureAwait(false);
        await decks.LoadAsync(request.DeckId, actor, request.Format, request.AllowHonourSystem, ct).ConfigureAwait(false);
        var lobby = Transition(() => GameTableLobby.Create(actor, host, request.DeckId, request.Capacity,
            request.Format, request.StartingLife, request.AllowHonourSystem));
        foreach (var invited in request.Invitees)
        {
            var account = invited.ToString();
            var name = await AccountAsync(account, ct).ConfigureAwait(false);
            lobby = Transition(() => lobby.Invite(actor, account, name));
        }
        await store.CreateAsync(lobby, ct).ConfigureAwait(false);
        return View(lobby, actor);
    }

    public async Task<GameLobbyDto> GetAsync(Guid id, string actor, CancellationToken ct = default) =>
        View(await FindAsync(id, actor, ct).ConfigureAwait(false), actor);

    public async Task<IReadOnlyList<GameLobbyDto>> ListAsync(string actor, CancellationToken ct = default) =>
        (await store.ListAsync(actor, ct).ConfigureAwait(false)).Select(lobby => View(lobby, actor)).ToArray();

    public async Task<GameLobbyDto> BringDeckAsync(Guid id, string actor, BringLobbyDeckRequest request, CancellationToken ct = default)
    {
        var lobby = await FindAsync(id, actor, ct).ConfigureAwait(false);
        Revision(lobby, request.Revision);
        await decks.LoadAsync(request.DeckId, actor, lobby.Format, lobby.AllowHonourSystem, ct).ConfigureAwait(false);
        var next = Transition(() => lobby.BringDeck(actor, request.DeckId));
        await SaveAsync(next, lobby.Revision, ct).ConfigureAwait(false);
        return View(next, actor);
    }

    public async Task<GameLobbyDto> LeaveAsync(Guid id, string actor, long revision, CancellationToken ct = default)
    {
        var lobby = await FindAsync(id, actor, ct).ConfigureAwait(false);
        Revision(lobby, revision);
        var next = Transition(() => lobby.Leave(actor));
        await SaveAsync(next, lobby.Revision, ct).ConfigureAwait(false);
        return View(next, actor);
    }

    private async Task<GameTableLobby> FindAsync(Guid id, string actor, CancellationToken ct) =>
        await store.FindAsync(id, actor, ct).ConfigureAwait(false) ?? throw new ResourceNotFoundException("Table not found.");

    private async Task<string> AccountAsync(string account, CancellationToken ct)
    {
        if (!Guid.TryParse(account, out var id))
            throw new InvalidRequestException("Choose a valid player account.");
        return await db.Users.AsNoTracking().Where(user => user.Id == id).Select(user => user.Username)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false) ?? throw new InvalidRequestException("That player account is unavailable.");
    }

    private async Task SaveAsync(GameTableLobby next, long revision, CancellationToken ct)
    {
        if (!await store.TryUpdateAsync(next, revision, ct).ConfigureAwait(false))
            throw new InvalidResourceStateException("The table changed. Refresh it before trying again.");
    }

    private static void Revision(GameTableLobby lobby, long expected)
    {
        if (lobby.Revision != expected)
            throw new InvalidResourceStateException("The table changed. Refresh it before trying again.");
    }

    private static GameTableLobby Transition(Func<GameTableLobby> transition)
    {
        try
        { return transition(); }
        catch (ArgumentException) { throw new InvalidRequestException("The table settings are invalid."); }
        catch (InvalidOperationException) { throw new InvalidResourceStateException("That table change is not available."); }
    }

    private static GameLobbyDto View(GameTableLobby lobby, string actor) => new(lobby.Id, lobby.HostUserId,
        lobby.Capacity, lobby.Format, lobby.StartingLife, lobby.AllowHonourSystem,
        lobby.Seats.Select(seat => new GameLobbySeatDto(seat.UserId, seat.Name, seat.DeckId is not null,
            seat.UserId == actor ? seat.DeckId : null)).ToArray(),
        lobby.Revision, lobby.Starting, lobby.Cancelled, lobby.GameId)
    { IsHost = lobby.HostUserId == actor };
}
