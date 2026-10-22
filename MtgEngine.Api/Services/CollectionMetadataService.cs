using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public sealed partial class CollectionMetadataService(MtgEngineDbContext db, TimeProvider clock, ICardHistoryService history)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    internal static string NormalizeLanguage(string value)
    {
        var language = value.Trim().ToLowerInvariant();
        if (language.Length is < 2 or > 8 || language.Any(c => !char.IsAsciiLetter(c) && c != '-'))
            throw new InvalidRequestException("Choose a valid card language.");
        return language;
    }
    private static string? NormalizeCondition(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var condition = value.ToUpperInvariant();
        return condition is "NM" or "LP" or "MP" or "HP" or "DMG" ? condition : throw new InvalidRequestException("Choose NM, LP, MP, HP or DMG.");
    }
    internal static string[] Tags(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];
    private static string[] NormalizeTags(IEnumerable<string>? tags)
    {
        var values = (tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        if (values.Length > 20 || values.Any(t => t.Length > 64))
            throw new InvalidRequestException("Use at most 20 tags, each up to 64 characters.");
        return values;
    }
    private async Task<Collection> OwnAsync(Guid id, string userId, CancellationToken ct) =>
        await db.Collections.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId && !c.IsDeck, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("Collection not found.");

    public async Task<CollectionMetadataRequest> GetAsync(Guid collectionId, Guid rowId, string owner, CancellationToken ct, string? finish = null)
    {
        await OwnAsync(collectionId, owner, ct).ConfigureAwait(false);
        var card = await db.CollectionCards.AsNoTracking().FirstOrDefaultAsync(c => c.Id == rowId && c.CollectionId == collectionId, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("Card not found in this collection.");
        if (finish is not null)
        {
            var selected = CollectionFinishData.Normalize(finish);
            var value = CollectionFinishData.Read(card, selected);
            return new(card.Language, value.Condition, value.PurchasePrice, value.Currency, value.Tags ?? [], selected, value.Notes);
        }
        return new(card.Language, card.Condition, card.PurchasePrice, card.PurchaseCurrency, Tags(card.TagsJson));
    }

    public async Task UpdateAsync(Guid collectionId, Guid rowId, string owner, CollectionMetadataRequest request, CancellationToken ct)
    {
        var collection = await OwnAsync(collectionId, owner, ct).ConfigureAwait(false);
        var card = await db.CollectionCards.FirstOrDefaultAsync(c => c.CollectionId == collectionId && c.Id == rowId, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("Card not found in this collection.");
        var language = NormalizeLanguage(request.Language);
        var condition = NormalizeCondition(request.Condition);
        var currency = string.IsNullOrWhiteSpace(request.Currency) ? null : request.Currency.Trim().ToUpperInvariant();
        if (currency is not null && (currency.Length != 3 || currency.Any(c => !char.IsAsciiLetter(c))))
            throw new InvalidRequestException("Use a three-letter currency code.");
        if (request.PurchasePrice.HasValue && currency is null)
            throw new InvalidRequestException("Choose the purchase currency.");
        if (request.Finish is not null)
        {
            await UpdateFinishAsync(collection, card, request, language, condition, currency, ct).ConfigureAwait(false);
            return;
        }
        if (await db.CollectionCards.AnyAsync(c => c.Id != rowId && c.CollectionId == collectionId && c.OracleId == card.OracleId && c.ScryfallId == card.ScryfallId && c.Board == card.Board && c.Language == language, ct).ConfigureAwait(false))
            throw new InvalidResourceStateException("That printing already has a row in this language. Edit that row instead.");
        card.Language = language;
        card.Condition = condition;
        card.PurchasePrice = request.PurchasePrice;
        card.PurchaseCurrency = currency;
        card.TagsJson = JsonSerializer.Serialize(NormalizeTags(request.Tags));
        CollectionFinishData.ApplyLegacy(card);
        collection.UpdatedAt = Now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<CollectionBulkResponse> BulkAsync(Guid collectionId, string owner, CollectionBulkRequest request, CancellationToken ct)
    {
        var ids = request.CardIds.Distinct().ToArray();
        if (ids.Length is < 1 or > 500)
            throw new InvalidRequestException("Select between 1 and 500 card rows.");
        if (request.Action is not ("tag" or "untag" or "condition" or "remove"))
            throw new InvalidRequestException("Choose a valid bulk action.");
        var tag = request.Tag?.Trim();
        if (request.Action is "tag" or "untag" && (string.IsNullOrEmpty(tag) || tag.Length > 64))
            throw new InvalidRequestException("Enter a tag up to 64 characters.");
        var condition = NormalizeCondition(request.Condition);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var collection = await OwnAsync(collectionId, owner, ct).ConfigureAwait(false);
        var rows = await db.CollectionCards.Where(c => c.CollectionId == collectionId && ids.Contains(c.Id)).ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count != ids.Length)
            throw new ResourceNotFoundException("One or more selected cards are no longer in this collection.");
        var before = Serialize(rows.Select(Snapshot.From));
        foreach (var row in rows)
            CollectionFinishData.PreserveOwned(row);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (request.Action != "remove")
                CollectionFinishData.Bulk(row, request.Action, tag, condition);
            if (request.Action == "tag")
                row.TagsJson = JsonSerializer.Serialize(NormalizeTags(Tags(row.TagsJson).Append(tag!)));
            else if (request.Action == "untag")
                row.TagsJson = JsonSerializer.Serialize(Tags(row.TagsJson).Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToArray());
            else if (request.Action == "condition")
                row.Condition = condition;
            else
            { Record(collection, row, CollectionCardEventType.Removed, -row.Quantity, -row.QuantityFoil, 0, 0); db.CollectionCards.Remove(row); }
        }
        collection.UpdatedAt = Now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        // Fingerprint the persisted representation: SQLite normalizes decimal scales and timestamps.
        var persisted = await db.CollectionCards.AsNoTracking().Where(c => c.CollectionId == collectionId && ids.Contains(c.Id)).ToArrayAsync(ct).ConfigureAwait(false);
        var after = Serialize(persisted.Select(Snapshot.From));
        var undo = new CollectionUndo { UserId = owner, CollectionId = collectionId, BeforeJson = before, AfterHash = Hash(after), ExpiresAt = Now.AddSeconds(5) };
        db.CollectionUndos.Add(undo);
        collection.UpdatedAt = Now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new(undo.Id, undo.ExpiresAt, ids.Length);
    }

    public async Task UndoAsync(Guid token, string owner, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var undo = await db.CollectionUndos.FirstOrDefaultAsync(u => u.Id == token && u.UserId == owner, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("Undo is not available.");
        if (undo.UsedAt.HasValue || undo.ExpiresAt <= Now)
            throw new InvalidResourceStateException("The undo window has ended.");
        var collection = await OwnAsync(undo.CollectionId, owner, ct).ConfigureAwait(false);
        var before = JsonSerializer.Deserialize<Snapshot[]>(undo.BeforeJson)!;
        var ids = before.Select(c => c.Id).ToArray();
        var current = await db.CollectionCards.AsNoTracking().Where(c => c.CollectionId == undo.CollectionId && ids.Contains(c.Id)).ToArrayAsync(ct).ConfigureAwait(false);
        if (Hash(Serialize(current.Select(Snapshot.From))) != undo.AfterHash)
            throw new InvalidResourceStateException("These cards changed after the bulk edit. Undo would overwrite newer work.");
        var existing = await db.CollectionCards.Where(c => c.CollectionId == undo.CollectionId && ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct).ConfigureAwait(false);
        foreach (var snapshot in before)
        {
            var restored = snapshot.ToCard();
            if (existing.TryGetValue(snapshot.Id, out var row))
            { db.Entry(row).CurrentValues.SetValues(restored); db.Entry(row).State = EntityState.Modified; }
            else
            { db.CollectionCards.Add(restored); Record(collection, restored, CollectionCardEventType.Added, restored.Quantity, restored.QuantityFoil, restored.Quantity, restored.QuantityFoil); }
        }
        undo.UsedAt = Now;
        collection.UpdatedAt = Now;
        try
        { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
        catch (DbUpdateException) { throw new InvalidResourceStateException("These printings changed after removal. Undo could not restore them safely."); }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private void Record(Collection collection, CollectionCard row, CollectionCardEventType kind, int regular, int foil, int after, int afterFoil) =>
        history.RecordFinishes(collection, row, kind, regular, foil, after, afterFoil, quantityEtchedDelta: kind == CollectionCardEventType.Removed ? -row.QuantityEtched : kind == CollectionCardEventType.Added ? row.QuantityEtched : 0, quantityEtchedAfter: kind == CollectionCardEventType.Removed ? 0 : row.QuantityEtched);
    private static string Serialize(IEnumerable<Snapshot> rows) => JsonSerializer.Serialize(rows.OrderBy(r => r.Id));
    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
}
