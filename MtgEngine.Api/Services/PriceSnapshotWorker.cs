using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Domain.Models;
using MtgEngine.Domain.ValueObjects;

namespace MtgEngine.Api.Services;

/// <summary>
/// Records one price snapshot per day for every printing that appears in a collection,
/// building the history behind the modal's price chart. Scryfall publishes only current
/// prices, so history exists from the day a printing is first seen here — never before.
/// Scope is deliberately collection-only: snapshotting the whole 250k-printing corpus
/// would add ~90M rows a year; owned printings keep the table bounded by what users
/// actually track.
/// </summary>
public sealed class PriceSnapshotWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICardLookup _cards;
    private readonly ILogger<PriceSnapshotWorker> _logger;

    /// <summary>Bulk prices refresh daily; checking more often only fills gaps sooner after restarts.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>Lets BulkDataService finish its startup load so snapshots read bulk prices, not API fallbacks.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    public PriceSnapshotWorker(
        IServiceScopeFactory scopeFactory,
        ICardLookup cards,
        ILogger<PriceSnapshotWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _cards = cards;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var added = await SnapshotAsync(stoppingToken);
                    if (added > 0)
                        _logger.LogInformation("Price snapshot: recorded {Count} printings", added);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PriceSnapshotWorker: snapshot pass failed");
                }
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // App shutting down — expected.
        }
    }

    /// <summary>
    /// One idempotent pass: today's row is inserted only for printings that don't have
    /// one yet, so restarts and the 6h cadence never duplicate a day.
    /// </summary>
    internal async Task<int> SnapshotAsync(CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtgEngineDbContext>();

        var pinned = await db.CollectionCards
            .Where(c => c.ScryfallId != null)
            .Select(c => c.ScryfallId!)
            .Distinct()
            .ToListAsync(ct);
        var unpinnedOracles = await db.CollectionCards
            .Where(c => c.ScryfallId == null)
            .Select(c => c.OracleId)
            .Distinct()
            .ToListAsync(ct);

        // Rows without a pinned printing display the default (newest) printing, so that
        // is the printing whose price matters for them.
        var ids = new HashSet<string>(pinned, StringComparer.OrdinalIgnoreCase);
        foreach (var oracleId in unpinnedOracles)
        {
            ct.ThrowIfCancellationRequested();
            var printings = await _cards.GetPrintingsAsync(oracleId);
            if (printings.Length > 0)
                ids.Add(printings[0].ScryfallId);
        }

        var alreadyCaptured = (await db.CardPriceSnapshots
                .Where(s => s.CapturedAt == today)
                .Select(s => s.ScryfallId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var scryfallId in ids)
        {
            if (alreadyCaptured.Contains(scryfallId))
                continue;
            ct.ThrowIfCancellationRequested();

            var def = await _cards.GetByScryfallIdAsync(scryfallId);
            var prices = def?.Prices;
            if (prices is null || prices == CardPrices.None)
                continue;

            db.CardPriceSnapshots.Add(new CardPriceSnapshot
            {
                ScryfallId = scryfallId,
                CapturedAt = today,
                Usd = prices.Usd,
                UsdFoil = prices.UsdFoil,
                UsdEtched = prices.UsdEtched,
                Eur = prices.Eur,
                EurFoil = prices.EurFoil,
                Tix = prices.Tix,
            });
            added++;
        }

        if (added > 0)
            await db.SaveChangesAsync(ct);
        return added;
    }
}
