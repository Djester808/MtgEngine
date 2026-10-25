using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Domain.Models;
using Snapshot = MtgEngine.Api.Services.CollectionMetadataService.Snapshot;
namespace MtgEngine.Api.Services;

public sealed record UndoableChange<T>(T Value, Guid Token, DateTime ExpiresAt);
public sealed class CollectionMutationUndoService(MtgEngineDbContext db, TimeProvider clock, ICardHistoryService history)
{
    private sealed record State(int Version, Guid[] Collections, string[] Oracles, Snapshot[] Before);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string Serialize(IEnumerable<Snapshot> rows) => JsonSerializer.Serialize(rows.OrderBy(r => r.Id));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private async Task<Collection[]> OwnAsync(string owner, Guid[] ids, CancellationToken ct)
    {
        var collections = await db.Collections.Where(c => ids.Contains(c.Id) && c.UserId == owner).ToArrayAsync(ct).ConfigureAwait(false);
        if (collections.Length != ids.Length)
            throw new ResourceNotFoundException("Collection not found.");
        return collections;
    }
    private async Task<Snapshot[]> RowsAsync(Guid[] ids, string[] oracles, CancellationToken ct) =>
        [.. (await db.CollectionCards.AsNoTracking().Where(c => ids.Contains(c.CollectionId) && oracles.Contains(c.OracleId)).ToArrayAsync(ct).ConfigureAwait(false)).Select(Snapshot.From)];
    public async Task<string[]> OraclesAsync(Guid collectionId, string owner, Guid[] rowIds, CancellationToken ct)
    {
        await OwnAsync(owner, [collectionId], ct).ConfigureAwait(false);
        var rows = await db.CollectionCards.AsNoTracking().Where(c => c.CollectionId == collectionId && rowIds.Contains(c.Id)).ToArrayAsync(ct).ConfigureAwait(false);
        if (rows.Length != rowIds.Distinct().Count())
            throw new ResourceNotFoundException("One or more cards no longer exist.");
        return [.. rows.Select(c => c.OracleId).Distinct()];
    }
    public async Task<UndoableChange<T>> RunAsync<T>(string owner, Guid[] collectionIds, string[] oracleIds, Func<Task<T>> mutate, bool addition, CancellationToken ct)
    {
        var ids = collectionIds.Distinct().ToArray();
        var oracles = oracleIds.Distinct().ToArray();
        if (ids.Length is < 1 or > 2 || oracles.Length is < 1 or > 500)
            throw new InvalidRequestException("Choose a valid set of cards.");
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await OwnAsync(owner, ids, ct).ConfigureAwait(false);
        var before = await RowsAsync(ids, oracles, ct).ConfigureAwait(false);
        var result = await mutate().ConfigureAwait(false);
        var after = await RowsAsync(ids, oracles, ct).ConfigureAwait(false);
        var undo = new CollectionUndo { UserId = owner, CollectionId = ids[0], BeforeJson = JsonSerializer.Serialize(new State(2, ids, oracles, before)), AfterHash = Hash(Serialize(after)), ExpiresAt = addition ? Now.AddHours(1) : Now.AddSeconds(5) };
        db.CollectionUndos.Add(undo);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new(result, undo.Id, undo.ExpiresAt);
    }
    public async Task<bool> TryUndoAsync(Guid token, string owner, CancellationToken ct)
    {
        var undo = await db.CollectionUndos.AsNoTracking().FirstOrDefaultAsync(u => u.Id == token && u.UserId == owner, ct).ConfigureAwait(false);
        if (undo is null || !undo.BeforeJson.StartsWith('{'))
            return false;
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var claimed = await db.CollectionUndos.Where(u => u.Id == token && u.UserId == owner && u.UsedAt == null && u.ExpiresAt > Now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.UsedAt, Now), ct).ConfigureAwait(false);
        if (claimed != 1)
            throw new InvalidResourceStateException("The undo window has ended.");
        var state = JsonSerializer.Deserialize<State>(undo.BeforeJson) ?? throw new InvalidResourceStateException("Undo data is unavailable.");
        if (state.Version != 2)
            throw new InvalidResourceStateException("Undo data is unavailable.");
        var collections = (await OwnAsync(owner, state.Collections, ct).ConfigureAwait(false)).ToDictionary(c => c.Id);
        var current = await RowsAsync(state.Collections, state.Oracles, ct).ConfigureAwait(false);
        if (Hash(Serialize(current)) != undo.AfterHash)
            throw new InvalidResourceStateException("These cards changed after the action. Undo would overwrite newer work.");
        var live = await db.CollectionCards.Where(c => state.Collections.Contains(c.CollectionId) && state.Oracles.Contains(c.OracleId)).ToDictionaryAsync(c => c.Id, ct).ConfigureAwait(false);
        var original = state.Before.ToDictionary(c => c.Id);
        foreach (var row in live.Values.Where(c => !original.ContainsKey(c.Id)))
        {
            history.RecordFinishes(collections[row.CollectionId], row, CollectionCardEventType.Removed, -row.Quantity, -row.QuantityFoil, 0, 0, quantityEtchedDelta: -row.QuantityEtched, quantityEtchedAfter: 0);
            db.CollectionCards.Remove(row);
        }
        foreach (var snapshot in state.Before)
        {
            var restored = snapshot.ToCard();
            if (live.TryGetValue(snapshot.Id, out var row))
            {
                var etchedChange = restored.QuantityEtched - row.QuantityEtched;
                var normal = restored.Quantity - row.Quantity;
                var foil = restored.QuantityFoil - row.QuantityFoil;
                db.Entry(row).CurrentValues.SetValues(restored);
                if (normal != 0 || foil != 0 || etchedChange != 0)
                    history.RecordFinishes(collections[row.CollectionId], row, CollectionCardEventType.QuantityChanged, normal, foil, restored.Quantity, restored.QuantityFoil, quantityEtchedDelta: etchedChange, quantityEtchedAfter: row.QuantityEtched);
            }
            else
            {
                db.CollectionCards.Add(restored);
                history.RecordFinishes(collections[restored.CollectionId], restored, CollectionCardEventType.Added, restored.Quantity, restored.QuantityFoil, restored.Quantity, restored.QuantityFoil, quantityEtchedDelta: restored.QuantityEtched, quantityEtchedAfter: restored.QuantityEtched);
            }
        }
        foreach (var collection in collections.Values)
            collection.UpdatedAt = Now;
        try
        { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
        catch (DbUpdateException) { throw new InvalidResourceStateException("The cards changed. Undo could not restore them safely."); }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }
}
