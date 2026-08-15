using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;

namespace MtgEngine.Api.Services;

public interface IPriceHistoryService
{
    /// <summary>Daily price points for one printing, oldest first, within the last <paramref name="days"/> days.</summary>
    Task<PricePointDto[]> GetHistoryAsync(string scryfallId, int days, CancellationToken ct);
}

/// <summary>
/// Reads the daily snapshots recorded by <see cref="PriceSnapshotWorker"/>. History only
/// exists for printings that have been in a collection since tracking began — everything
/// else legitimately returns an empty series.
/// </summary>
public sealed class PriceHistoryService : IPriceHistoryService
{
    /// <summary>Matches the retention sweep — asking for more than we ever keep is a client bug.</summary>
    public const int MaxDays = 1825;

    private readonly MtgEngineDbContext _db;

    public PriceHistoryService(MtgEngineDbContext db) => _db = db;

    public async Task<PricePointDto[]> GetHistoryAsync(string scryfallId, int days, CancellationToken ct)
    {
        var clamped = Math.Clamp(days, 1, MaxDays);
        var cutoff = DateTime.UtcNow.Date.AddDays(-clamped);

        return await _db.CardPriceSnapshots
            .AsNoTracking()
            .Where(s => s.ScryfallId == scryfallId && s.CapturedAt >= cutoff)
            .OrderBy(s => s.CapturedAt)
            .Select(s => new PricePointDto(s.CapturedAt, s.Usd, s.UsdFoil, s.Eur, s.Tix))
            .ToArrayAsync(ct);
    }
}
