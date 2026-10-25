using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public interface IGameLobbyStore
{
    Task CreateAsync(GameTableLobby lobby, CancellationToken ct = default);

    Task<GameTableLobby?> FindAsync(Guid id, string account, CancellationToken ct = default);

    Task<IReadOnlyList<GameTableLobby>> ListAsync(string account, CancellationToken ct = default);

    Task<bool> TryUpdateAsync(GameTableLobby next, long expectedRevision, CancellationToken ct = default);
}

/// <summary>Lobby snapshots and their membership index are committed together under a revision check.</summary>
public sealed class GameLobbyStore(IServiceScopeFactory scopes) : IGameLobbyStore
{
    public async Task CreateAsync(GameTableLobby lobby, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();
        db.GameLobbies.Add(new PersistedGameLobby
        {
            Id = lobby.Id,
            Revision = lobby.Revision,
            StateJson = JsonSerializer.Serialize(lobby),
            Members = Members(lobby),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<GameTableLobby?> FindAsync(Guid id, string account, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();
        var row = await db.GameLobbies.AsNoTracking()
            .Where(lobby => lobby.Id == id && lobby.Members.Any(member => member.UserId == account))
            .Select(lobby => lobby.StateJson).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : Read(row);
    }

    public async Task<IReadOnlyList<GameTableLobby>> ListAsync(string account, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();
        var rows = await db.GameLobbies.AsNoTracking()
            .Where(lobby => lobby.Members.Any(member => member.UserId == account))
            .OrderByDescending(lobby => lobby.UpdatedAt).Take(50)
            .Select(lobby => lobby.StateJson).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(Read).ToArray();
    }

    public async Task<bool> TryUpdateAsync(GameTableLobby next, long expectedRevision, CancellationToken ct = default)
    {
        if (next.Revision != expectedRevision + 1)
            throw new InvalidOperationException("A lobby update must advance one revision.");
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(next);
        var changed = await db.GameLobbies.Where(lobby => lobby.Id == next.Id && lobby.Revision == expectedRevision)
            .ExecuteUpdateAsync(update => update.SetProperty(lobby => lobby.StateJson, json)
                .SetProperty(lobby => lobby.Revision, next.Revision).SetProperty(lobby => lobby.UpdatedAt, DateTime.UtcNow), ct)
            .ConfigureAwait(false);
        if (changed == 0)
            return false;
        await db.GameLobbyMembers.Where(member => member.LobbyId == next.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        db.GameLobbyMembers.AddRange(Members(next));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static GameTableLobby Read(string json) =>
        JsonSerializer.Deserialize<GameTableLobby>(json) ?? throw new InvalidOperationException("Stored lobby is missing.");

    private static GameLobbyMember[] Members(GameTableLobby lobby) =>
        [.. lobby.Seats.Select(seat => new GameLobbyMember { LobbyId = lobby.Id, UserId = seat.UserId })];
}
