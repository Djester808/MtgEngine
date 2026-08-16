using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public interface ICardHistoryService
{
    /// <summary>
    /// Stages one event onto the context without saving. The caller's own
    /// <c>SaveChangesAsync</c> persists it, so the event and the change it describes land
    /// together instead of the log drifting from the data.
    /// </summary>
    /// <param name="card">
    /// Supplies only the card's identity (oracle id, printing, board). The copy counts are
    /// passed separately because they are wrong to read off the entity at every call site —
    /// on a removal the entity still holds the copies it is about to lose.
    /// </param>
    /// <param name="setCode">
    /// Only passed where a card definition is already in hand (the add path). Resolving it
    /// on every event would put a Scryfall lookup inside merge loops that already iterate
    /// hundreds of rows; a null set code costs the UI one line of detail, not the entry.
    /// </param>
    void Record(
        Collection collection,
        CollectionCard card,
        CollectionCardEventType eventType,
        int quantityDelta,
        int quantityFoilDelta,
        int quantityAfter,
        int quantityFoilAfter,
        Collection? counterpart = null,
        string? setCode = null,
        decimal? priceUsd = null);

    /// <summary>Everything that has happened to one card for one user, newest first.</summary>
    Task<CardHistoryEntryDto[]> GetForCardAsync(
        string userId, string oracleId, int limit, CancellationToken ct);
}

/// <summary>
/// Writes and reads the append-only <see cref="CollectionCardEvent"/> trail behind the
/// card modal's History tab.
/// </summary>
/// <remarks>
/// History only exists from the day recording shipped. Nothing reconstructs the past: a
/// <see cref="CollectionCard"/> row knows only its current state, so backfilling would mean
/// inventing events that never happened. An empty tab on a long-owned card is correct, not
/// a bug.
/// </remarks>
public sealed class CardHistoryService : ICardHistoryService
{
    /// <summary>Hard ceiling on one read, so a long-lived card cannot return an unbounded scan.</summary>
    public const int MaxLimit = 500;

    private readonly MtgEngineDbContext _db;

    public CardHistoryService(MtgEngineDbContext db) => _db = db;

    public void Record(
        Collection collection,
        CollectionCard card,
        CollectionCardEventType eventType,
        int quantityDelta,
        int quantityFoilDelta,
        int quantityAfter,
        int quantityFoilAfter,
        Collection? counterpart = null,
        string? setCode = null,
        decimal? priceUsd = null)
    {
        _db.CollectionCardEvents.Add(new CollectionCardEvent
        {
            UserId = collection.UserId,
            CollectionId = collection.Id,
            // Snapshots, not joins: the collection may be renamed or deleted later, and a
            // dangling id tells the user nothing.
            CollectionName = collection.Name,
            IsDeck = collection.IsDeck,
            OracleId = card.OracleId,
            ScryfallId = card.ScryfallId,
            SetCode = setCode,
            Board = card.Board,
            EventType = eventType,
            QuantityDelta = quantityDelta,
            QuantityFoilDelta = quantityFoilDelta,
            QuantityAfter = quantityAfter,
            QuantityFoilAfter = quantityFoilAfter,
            CounterpartCollectionId = counterpart?.Id,
            CounterpartCollectionName = counterpart?.Name,
            PriceUsd = priceUsd,
            CreatedAt = DateTime.UtcNow,
        });
    }

    public async Task<CardHistoryEntryDto[]> GetForCardAsync(
        string userId, string oracleId, int limit, CancellationToken ct)
    {
        var clamped = Math.Clamp(limit, 1, MaxLimit);

        // CreatedAt arrives already marked UTC — MtgEngineDbContext converts every DateTime
        // on read, so no endpoint has to remember to do it.
        return await _db.CollectionCardEvents
            .AsNoTracking()
            .Where(e => e.UserId == userId && e.OracleId == oracleId)
            // Id breaks ties: a move writes its two halves in the same transaction, so
            // CreatedAt alone can order "moved in" above the "moved out" that caused it.
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Take(clamped)
            .Select(e => new CardHistoryEntryDto(
                e.Id,
                e.EventType,
                e.CollectionId,
                e.CollectionName,
                e.IsDeck,
                e.Board,
                e.ScryfallId,
                e.SetCode,
                e.QuantityDelta,
                e.QuantityFoilDelta,
                e.QuantityAfter,
                e.QuantityFoilAfter,
                e.CounterpartCollectionId,
                e.CounterpartCollectionName,
                e.PriceUsd,
                e.CreatedAt))
            .ToArrayAsync(ct);
    }
}
