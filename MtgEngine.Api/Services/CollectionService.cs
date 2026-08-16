using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Mapping;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public interface ICollectionService
{
    // Collections (IsDeck = false only)
    Task<CollectionDto[]> GetUserCollectionsAsync(string userId);
    Task<CollectionDetailDto?> GetCollectionAsync(Guid collectionId, string userId);

    /// <summary>
    /// Every oracle id the user owns a copy of, across all their collections. Decks are
    /// excluded: a card being *in* a deck is not evidence of owning it, which is the
    /// whole point of the caller's question.
    /// </summary>
    Task<string[]> GetOwnedOracleIdsAsync(string userId, CancellationToken ct = default);
    Task<CollectionDetailDto> CreateCollectionAsync(string userId, CreateCollectionRequest request);
    Task<CollectionDetailDto> UpdateCollectionAsync(Guid collectionId, string userId, UpdateCollectionRequest request);
    Task<bool> DeleteCollectionAsync(Guid collectionId, string userId);

    // Shared card management (used by both collections and decks).
    // Created distinguishes a new row (201) from an increment of an existing one (200).
    Task<(CollectionCardDto Card, bool Created)> AddCardToCollectionAsync(
        Guid collectionId,
        string userId,
        AddCardToCollectionRequest request);
    Task<CollectionCardDto> UpdateCollectionCardAsync(
        Guid collectionId,
        Guid cardId,
        string userId,
        UpdateCollectionCardRequest request);
    Task<bool> RemoveCardFromCollectionAsync(Guid collectionId, Guid cardId, string userId);

    /// <summary>
    /// Removes every row of a card (all printings). Pass <paramref name="board"/> to
    /// scope the removal to one board; null removes across all boards.
    /// </summary>
    Task<bool> RemoveCardByOracleAsync(
        Guid collectionId, string oracleId, string userId, string? board = null);

    /// <summary>
    /// Moves copies of one card row into another collection, folding into the matching
    /// printing there when one exists.
    /// </summary>
    Task<MoveCardResultDto> MoveCardAsync(
        Guid collectionId, Guid cardId, string userId, MoveCardRequest request, CancellationToken ct = default);

    /// <summary>Moves several whole card rows to another collection in one request.</summary>
    Task<MoveCardsResultDto> MoveCardsAsync(
        Guid collectionId, string userId, MoveCardsRequest request, CancellationToken ct = default);

    /// <summary>Folds every card of one collection into another, optionally deleting the emptied source.</summary>
    Task<MergeCollectionsResultDto> MergeCollectionsAsync(
        Guid targetCollectionId, string userId, MergeCollectionsRequest request, CancellationToken ct = default);

    // Decks (IsDeck = true)
    Task<DeckDto[]> GetUserDecksAsync(string userId);
    Task<DeckDetailDto?> GetDeckAsync(Guid deckId, string userId);
    Task<DeckDetailDto> CreateDeckAsync(string userId, CreateDeckRequest request);
    Task<DeckDetailDto> UpdateDeckAsync(Guid deckId, string userId, UpdateDeckRequest request);
    Task<bool> DeleteDeckAsync(Guid deckId, string userId);
}

public sealed class CollectionService : ICollectionService
{
    private readonly MtgEngineDbContext _context;
    private readonly ICardLookup _scryfallService;
    private readonly ICardHistoryService _history;

    public CollectionService(
        MtgEngineDbContext context, ICardLookup scryfallService, ICardHistoryService history)
    {
        _context = context;
        _scryfallService = scryfallService;
        _history = history;
    }

    // ---- Collections ----

    public async Task<CollectionDto[]> GetUserCollectionsAsync(string userId)
    {
        var collections = await _context.Collections
            .Where(c => c.UserId == userId && !c.IsDeck)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new CollectionDto
            {
                Id = c.Id,
                Name = c.Name,
                Description = c.Description,
                CoverUri = c.CoverUri,
                CardCount = c.Cards.Sum(cc => cc.Quantity + cc.QuantityFoil),
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt
            })
            .ToArrayAsync();

        return collections;
    }

    public async Task<string[]> GetOwnedOracleIdsAsync(string userId, CancellationToken ct = default)
    {
        // A row with no copies left (quantity and foil both zero) is a placeholder, not
        // ownership — the deck grid would otherwise show those cards as owned.
        return await _context.CollectionCards
            .Where(cc => cc.Collection.UserId == userId
                && !cc.Collection.IsDeck
                && cc.Quantity + cc.QuantityFoil > 0)
            .Select(cc => cc.OracleId)
            .Distinct()
            .ToArrayAsync(ct);
    }

    public async Task<CollectionDetailDto?> GetCollectionAsync(Guid collectionId, string userId)
    {
        var collection = await _context.Collections
            .AsNoTracking()
            .Where(c => c.Id == collectionId && c.UserId == userId)
            .Include(c => c.Cards)
            .FirstOrDefaultAsync();

        if (collection == null)
            return null;

        var cards = new List<CollectionCardDto>();
        foreach (var card in collection.Cards)
        {
            var cardDef = await _scryfallService.ResolveForEntryAsync(card);
            cards.Add(DomainMapper.ToDto(card, cardDef));
        }

        return new CollectionDetailDto
        {
            Id = collection.Id,
            Name = collection.Name,
            Description = collection.Description,
            CoverUri = collection.CoverUri,
            CreatedAt = collection.CreatedAt,
            UpdatedAt = collection.UpdatedAt,
            Cards = [.. cards]
        };
    }

    public async Task<CollectionDetailDto> CreateCollectionAsync(string userId, CreateCollectionRequest request)
    {
        var collection = new Collection(userId, request.Name, request.Description, isDeck: false);
        _context.Collections.Add(collection);
        await _context.SaveChangesAsync();

        return new CollectionDetailDto
        {
            Id = collection.Id,
            Name = collection.Name,
            Description = collection.Description,
            CoverUri = collection.CoverUri,
            CreatedAt = collection.CreatedAt,
            UpdatedAt = collection.UpdatedAt,
            Cards = []
        };
    }

    public async Task<CollectionDetailDto> UpdateCollectionAsync(
        Guid collectionId,
        string userId,
        UpdateCollectionRequest request)
    {
        var collection = await _context.Collections
            .Where(c => c.Id == collectionId && c.UserId == userId)
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("Collection not found");

        collection.Name = request.Name;
        collection.Description = request.Description;
        collection.CoverUri = request.CoverUri;
        collection.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        // Return updated details
        return await GetCollectionAsync(collectionId, userId)
            ?? throw new InvalidOperationException("Failed to retrieve updated collection");
    }

    public async Task<bool> DeleteCollectionAsync(Guid collectionId, string userId)
    {
        var collection = await _context.Collections
            .Where(c => c.Id == collectionId && c.UserId == userId)
            .FirstOrDefaultAsync();

        if (collection == null)
            return false;

        await RecordCardsLostWithCollectionAsync(collection);

        _context.Collections.Remove(collection);
        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Records a Removed event for every card in a collection that is about to be deleted.
    /// The cards go with it via cascade, so without this the copies simply vanish from
    /// history — and "which collection did I delete this out of" is the question the tab
    /// exists to answer. Events carry no foreign key to the collection precisely so they
    /// outlive it.
    /// </summary>
    private async Task RecordCardsLostWithCollectionAsync(Collection collection)
    {
        var cards = await _context.CollectionCards
            .AsNoTracking()
            .Where(cc => cc.CollectionId == collection.Id)
            .ToListAsync();

        foreach (var card in cards)
            _history.Record(
                collection, card, CollectionCardEventType.Removed,
                -card.Quantity, -card.QuantityFoil, 0, 0);
    }

    // ---- Collection Cards ----

    /// <summary>
    /// The only board values the schema means. Anything else is normalized to main on
    /// write — the previous pass-through let values like "Main" or "commander" create
    /// rows that bypassed the per-printing unique index and every board filter (a data
    /// migration already had to repair such rows once).
    /// </summary>
    internal static string NormalizeBoard(string? board)
    {
        var b = board?.Trim().ToLowerInvariant();
        return b is "side" or "maybe" ? b : "main";
    }

    public async Task<(CollectionCardDto Card, bool Created)> AddCardToCollectionAsync(
        Guid collectionId,
        string userId,
        AddCardToCollectionRequest request)
    {
        // Verify collection exists and belongs to user
        var collection = await _context.Collections
            .Where(c => c.Id == collectionId && c.UserId == userId)
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("Collection not found");

        var board = NormalizeBoard(request.Board);

        // Increment in SQL, not read-modify-write: two simultaneous adds of the same
        // printing both computed quantity+1 from the same read and one increment was
        // silently lost.
        async Task<bool> TryIncrementAsync()
        {
            var n = await _context.CollectionCards
                .Where(cc => cc.CollectionId == collectionId
                          && cc.OracleId == request.OracleId
                          && cc.ScryfallId == request.ScryfallId
                          && cc.Board == board)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Quantity, c => c.Quantity + request.Quantity)
                    .SetProperty(c => c.QuantityFoil, c => c.QuantityFoil + request.QuantityFoil));
            return n > 0;
        }

        // No row pins this printing yet, but an unpinned row for the same card may exist —
        // "owned, printing unspecified". Adopt it (pinning the printing) instead of adding
        // a sibling: the two are not two printings, and as separate rows they rendered as
        // duplicate tiles with the count split between them.
        async Task<bool> TryAdoptUnpinnedAsync()
        {
            if (request.ScryfallId is null)
                return false;
            var n = await _context.CollectionCards
                .Where(cc => cc.CollectionId == collectionId
                          && cc.OracleId == request.OracleId
                          && cc.ScryfallId == null
                          && cc.Board == board)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.ScryfallId, request.ScryfallId)
                    .SetProperty(c => c.Quantity, c => c.Quantity + request.Quantity)
                    .SetProperty(c => c.QuantityFoil, c => c.QuantityFoil + request.QuantityFoil));
            return n > 0;
        }

        var created = false;
        if (!await TryIncrementAsync() && !await TryAdoptUnpinnedAsync())
        {
            var fresh = new CollectionCard(
                collectionId,
                request.OracleId,
                request.ScryfallId,
                request.Quantity,
                request.QuantityFoil,
                request.Notes,
                board);
            // Baseline for the price-change display: snapshot today's market price once,
            // on the row's creation. Increments to an existing row keep the original.
            var defAtAdd = request.ScryfallId is not null
                ? await _scryfallService.GetByScryfallIdAsync(request.ScryfallId)
                : await _scryfallService.GetByOracleIdAsync(request.OracleId);
            fresh.PriceUsdAtAdd = defAtAdd?.Prices.Usd;
            fresh.PriceUsdFoilAtAdd = defAtAdd?.Prices.UsdFoil;
            _context.CollectionCards.Add(fresh);
            collection.UpdatedAt = DateTime.UtcNow;
            try
            {
                await _context.SaveChangesAsync();
                created = true;
            }
            catch (DbUpdateException)
            {
                // The unique index (CollectionId, ScryfallId, Board) rejected the insert.
                // Usual cause: a concurrent add of the same printing — fold into the winner.
                _context.Entry(fresh).State = EntityState.Detached;
                if (!await TryIncrementAsync())
                    // The index collided but our (OracleId-scoped) increment still matches
                    // nothing: the printing is saved under a *different* oracle id. That is a
                    // bad request, not a race — surface it instead of NRE-ing on FirstAsync.
                    throw new InvalidResourceStateException(
                        "That printing is already saved under a different card.");
                // Persist the collection's UpdatedAt bump the failed SaveChanges rolled back
                // (fresh is detached, so this writes only the timestamp).
                await _context.SaveChangesAsync();
            }
        }
        else
        {
            collection.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        var cardRecord = await _context.CollectionCards
            .AsNoTracking()
            .FirstAsync(cc => cc.CollectionId == collectionId
                           && cc.OracleId == request.OracleId
                           && cc.ScryfallId == request.ScryfallId
                           && cc.Board == board);

        // Pinned printing first — resolving by oracle id here made the added card render
        // with the default printing's art until the next full reload.
        var cardDef = await _scryfallService.ResolveForEntryAsync(cardRecord);

        // Recorded here rather than beside each branch above: the increment paths run as
        // ExecuteUpdate, which bypasses the change tracker, so the post-write row read is
        // the first point where the resulting copy counts are known for every path. Costs
        // one extra save on add; the alternative is a log that guesses at its own numbers.
        _history.Record(
            collection,
            cardRecord,
            created ? CollectionCardEventType.Added : CollectionCardEventType.QuantityChanged,
            request.Quantity,
            request.QuantityFoil,
            cardRecord.Quantity,
            cardRecord.QuantityFoil,
            setCode: cardDef?.SetCode,
            priceUsd: cardDef?.Prices.Usd);
        await _context.SaveChangesAsync();

        return (DomainMapper.ToDto(cardRecord, cardDef), created);
    }

    public async Task<CollectionCardDto> UpdateCollectionCardAsync(
        Guid collectionId,
        Guid cardId,
        string userId,
        UpdateCollectionCardRequest request)
    {
        var card = await _context.CollectionCards
            .Where(cc => cc.Id == cardId && cc.CollectionId == collectionId)
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("Collection card not found");

        // Verify collection belongs to user
        var collection = await _context.Collections
            .Where(c => c.Id == collectionId && c.UserId == userId)
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("Collection not found");

        var qtyDelta = request.Quantity - card.Quantity;
        var foilDelta = request.QuantityFoil - card.QuantityFoil;
        var rePinned = request.ScryfallId is not null && request.ScryfallId != card.ScryfallId;

        card.Quantity = request.Quantity;
        card.QuantityFoil = request.QuantityFoil;
        card.Notes = request.Notes;
        if (request.ScryfallId is not null)
            card.ScryfallId = request.ScryfallId;
        collection.UpdatedAt = DateTime.UtcNow;

        // One edit can do both, and they are different questions later ("when did I get the
        // third copy" vs "when did this become the foil printing"), so they are two events.
        // A notes-only edit records neither — nothing about the card itself moved.
        if (rePinned)
            _history.Record(
                collection, card, CollectionCardEventType.PrintingChanged,
                0, 0, card.Quantity, card.QuantityFoil);
        if (qtyDelta != 0 || foilDelta != 0)
            _history.Record(
                collection, card, CollectionCardEventType.QuantityChanged,
                qtyDelta, foilDelta, card.Quantity, card.QuantityFoil);

        await _context.SaveChangesAsync();

        var cardDef = await _scryfallService.ResolveForEntryAsync(card);
        return DomainMapper.ToDto(card, cardDef);
    }

    public async Task<bool> RemoveCardFromCollectionAsync(Guid collectionId, Guid cardId, string userId)
    {
        var collection = await _context.Collections
            .Where(c => c.Id == collectionId && c.UserId == userId)
            .FirstOrDefaultAsync();

        if (collection == null)
            return false;

        var card = await _context.CollectionCards
            .Where(cc => cc.Id == cardId && cc.CollectionId == collectionId)
            .FirstOrDefaultAsync();

        if (card == null)
            return false;

        _history.Record(
            collection, card, CollectionCardEventType.Removed,
            -card.Quantity, -card.QuantityFoil, 0, 0);

        _context.CollectionCards.Remove(card);
        collection.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemoveCardByOracleAsync(
        Guid collectionId, string oracleId, string userId, string? board = null)
    {
        var collection = await _context.Collections
            .Where(c => c.Id == collectionId && c.UserId == userId)
            .FirstOrDefaultAsync();

        if (collection == null)
            return false;

        // All matching rows in one statement. The old FirstOrDefault + Remove deleted a
        // single arbitrary row — for a card present as several printings or on several
        // boards, "remove all copies" left the rest behind, and the AI refine path could
        // delete a sideboard row while meaning the main-board one.
        var query = _context.CollectionCards
            .Where(cc => cc.CollectionId == collectionId && cc.OracleId == oracleId);
        if (board is not null)
            query = query.Where(cc => cc.Board == board);

        // Read before the bulk delete: ExecuteDelete never materializes the rows, so this
        // is the only chance to say what was lost. Scoped to one card across its printings
        // and boards, so it is a handful of rows, not a table scan.
        var doomed = await query.AsNoTracking().ToListAsync();
        if (doomed.Count == 0)
            return false;

        var removed = await query.ExecuteDeleteAsync();
        if (removed == 0)
            return false;

        foreach (var row in doomed)
            _history.Record(
                collection, row, CollectionCardEventType.Removed,
                -row.Quantity, -row.QuantityFoil, 0, 0);

        collection.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return true;
    }

    // ---- Moving and merging ----

    /// <summary>
    /// Loads a collection the caller owns, or throws. Decks and collections share the
    /// table, so moves work between either — what matters is ownership.
    /// </summary>
    private async Task<Collection> OwnedCollectionAsync(Guid collectionId, string userId, string label, CancellationToken ct)
        => await _context.Collections
               .Where(c => c.Id == collectionId && c.UserId == userId)
               .FirstOrDefaultAsync(ct)
           ?? throw new ResourceNotFoundException($"{label} collection not found");

    /// <summary>
    /// Folds copies into <paramref name="targetCollectionId"/>, reusing the row for the
    /// same printing and board when one exists. Acquisition data rides with the copies: a moved
    /// row keeps its original <see cref="CollectionCard.AddedAt"/> and price-at-add,
    /// because it is the same physical card — resetting them would silently restate when
    /// it was acquired and wipe the baseline the price-change display compares against.
    /// When two rows fold together the earlier acquisition wins, so the surviving row
    /// still describes the oldest copy in it.
    /// </summary>
    /// <returns>The destination row, and whether it had to be created.</returns>
    private async Task<(CollectionCard Row, bool Created)> FoldIntoAsync(
        Guid targetCollectionId, CollectionCard source, int quantity, int quantityFoil, CancellationToken ct)
    {
        var existing = await _context.CollectionCards
            .Where(cc => cc.CollectionId == targetCollectionId
                      && cc.ScryfallId == source.ScryfallId
                      && cc.OracleId == source.OracleId
                      && cc.Board == source.Board)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            existing.Quantity += quantity;
            existing.QuantityFoil += quantityFoil;
            if (source.AddedAt < existing.AddedAt)
            {
                existing.AddedAt = source.AddedAt;
                existing.PriceUsdAtAdd = source.PriceUsdAtAdd;
                existing.PriceUsdFoilAtAdd = source.PriceUsdFoilAtAdd;
            }
            return (existing, false);
        }

        var moved = new CollectionCard(
            targetCollectionId, source.OracleId, source.ScryfallId,
            quantity, quantityFoil, source.Notes, source.Board)
        {
            AddedAt = source.AddedAt,
            PriceUsdAtAdd = source.PriceUsdAtAdd,
            PriceUsdFoilAtAdd = source.PriceUsdFoilAtAdd,
        };
        _context.CollectionCards.Add(moved);
        return (moved, true);
    }

    public async Task<MoveCardResultDto> MoveCardAsync(
        Guid collectionId, Guid cardId, string userId, MoveCardRequest request, CancellationToken ct = default)
    {
        if (request.TargetCollectionId == collectionId)
            throw new InvalidResourceStateException("A card cannot be moved into the collection it is already in.");

        var source = await OwnedCollectionAsync(collectionId, userId, "Source", ct);
        var target = await OwnedCollectionAsync(request.TargetCollectionId, userId, "Target", ct);

        var card = await _context.CollectionCards
            .Where(cc => cc.Id == cardId && cc.CollectionId == collectionId)
            .FirstOrDefaultAsync(ct)
            ?? throw new ResourceNotFoundException("Card not found in the source collection");

        // Omitted quantities mean "move the whole row".
        var moveQty = request.Quantity ?? card.Quantity;
        var moveFoil = request.QuantityFoil ?? card.QuantityFoil;

        if (moveQty > card.Quantity || moveFoil > card.QuantityFoil)
            throw new InvalidResourceStateException("Cannot move more copies than the collection holds.");
        if (moveQty <= 0 && moveFoil <= 0)
            throw new InvalidResourceStateException("Nothing to move — specify at least one copy.");

        var (targetRow, _) = await FoldIntoAsync(request.TargetCollectionId, card, moveQty, moveFoil, ct);

        card.Quantity -= moveQty;
        card.QuantityFoil -= moveFoil;
        var emptied = card.Quantity <= 0 && card.QuantityFoil <= 0;
        if (emptied)
            _context.CollectionCards.Remove(card);

        // Both halves, each naming the other end — the source's history should read "moved
        // to X" and the target's "moved from Y", and neither can be inferred from the other.
        _history.Record(
            source, card, CollectionCardEventType.MovedOut,
            -moveQty, -moveFoil, card.Quantity, card.QuantityFoil, counterpart: target);
        _history.Record(
            target, targetRow, CollectionCardEventType.MovedIn,
            moveQty, moveFoil, targetRow.Quantity, targetRow.QuantityFoil, counterpart: source);

        var now = DateTime.UtcNow;
        source.UpdatedAt = now;
        target.UpdatedAt = now;
        await _context.SaveChangesAsync(ct);

        var def = await _scryfallService.ResolveForEntryAsync(targetRow);
        return new MoveCardResultDto
        {
            Target = DomainMapper.ToDto(targetRow, def),
            SourceRemainder = emptied ? null : DomainMapper.ToDto(card, def),
        };
    }

    public async Task<MoveCardsResultDto> MoveCardsAsync(
        Guid collectionId, string userId, MoveCardsRequest request, CancellationToken ct = default)
    {
        if (request.TargetCollectionId == collectionId)
            throw new InvalidResourceStateException("A card cannot be moved into the collection it is already in.");
        if (request.CardIds.Length == 0)
            throw new InvalidResourceStateException("Select at least one card to move.");

        var source = await OwnedCollectionAsync(collectionId, userId, "Source", ct);
        var target = await OwnedCollectionAsync(request.TargetCollectionId, userId, "Target", ct);

        var ids = request.CardIds.Distinct().ToArray();
        var cards = await _context.CollectionCards
            .Where(cc => cc.CollectionId == collectionId && ids.Contains(cc.Id))
            .ToListAsync(ct);

        // Silence here would move some cards and quietly skip others — say which are gone.
        if (cards.Count != ids.Length)
            throw new ResourceNotFoundException("One or more selected cards are no longer in this collection.");

        var moved = 0;
        var folded = 0;
        var copies = 0;
        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();
            var (targetRow, created) = await FoldIntoAsync(
                request.TargetCollectionId, card, card.Quantity, card.QuantityFoil, ct);
            if (created)
                moved++;
            else
                folded++;
            copies += card.Quantity + card.QuantityFoil;

            _history.Record(
                source, card, CollectionCardEventType.MovedOut,
                -card.Quantity, -card.QuantityFoil, 0, 0, counterpart: target);
            _history.Record(
                target, targetRow, CollectionCardEventType.MovedIn,
                card.Quantity, card.QuantityFoil,
                targetRow.Quantity, targetRow.QuantityFoil, counterpart: source);
        }

        _context.CollectionCards.RemoveRange(cards);

        var now = DateTime.UtcNow;
        source.UpdatedAt = now;
        target.UpdatedAt = now;
        await _context.SaveChangesAsync(ct);

        return new MoveCardsResultDto
        {
            CardsMoved = moved,
            CardsFolded = folded,
            CopiesTransferred = copies,
            RemovedCardIds = [.. cards.Select(c => c.Id)],
        };
    }

    public async Task<MergeCollectionsResultDto> MergeCollectionsAsync(
        Guid targetCollectionId, string userId, MergeCollectionsRequest request, CancellationToken ct = default)
    {
        if (request.SourceCollectionId == targetCollectionId)
            throw new InvalidResourceStateException("A collection cannot be merged into itself.");

        var target = await OwnedCollectionAsync(targetCollectionId, userId, "Target", ct);
        var source = await OwnedCollectionAsync(request.SourceCollectionId, userId, "Source", ct);

        var sourceCards = await _context.CollectionCards
            .Where(cc => cc.CollectionId == request.SourceCollectionId)
            .ToListAsync(ct);

        var moved = 0;
        var folded = 0;
        var copies = 0;
        foreach (var card in sourceCards)
        {
            ct.ThrowIfCancellationRequested();
            var (targetRow, created) = await FoldIntoAsync(targetCollectionId, card, card.Quantity, card.QuantityFoil, ct);
            if (created)
                moved++;
            else
                folded++;
            copies += card.Quantity + card.QuantityFoil;

            _history.Record(
                source, card, CollectionCardEventType.MovedOut,
                -card.Quantity, -card.QuantityFoil, 0, 0, counterpart: target);
            _history.Record(
                target, targetRow, CollectionCardEventType.MovedIn,
                card.Quantity, card.QuantityFoil,
                targetRow.Quantity, targetRow.QuantityFoil, counterpart: source);
        }

        // The source rows are gone either way — the copies now live in the target.
        _context.CollectionCards.RemoveRange(sourceCards);

        var now = DateTime.UtcNow;
        target.UpdatedAt = now;
        source.UpdatedAt = now;
        if (request.DeleteSource)
            _context.Collections.Remove(source);

        await _context.SaveChangesAsync(ct);

        return new MergeCollectionsResultDto
        {
            CardsMoved = moved,
            CardsFolded = folded,
            CopiesTransferred = copies,
            SourceDeleted = request.DeleteSource,
            Target = await GetCollectionAsync(targetCollectionId, userId)
                     ?? throw new InvalidOperationException("Merged collection could not be reloaded"),
        };
    }

    // ---- Deck methods ----

    public async Task<DeckDto[]> GetUserDecksAsync(string userId)
    {
        return await _context.Collections
            .Where(c => c.UserId == userId && c.IsDeck)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new DeckDto
            {
                Id = c.Id,
                Name = c.Name,
                CoverUri = c.CoverUri,
                Format = c.Format,
                CommanderOracleId = c.CommanderOracleId,
                CardCount = c.Cards.Sum(cc => cc.Quantity + cc.QuantityFoil),
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
            })
            .ToArrayAsync();
    }

    public async Task<DeckDetailDto?> GetDeckAsync(Guid deckId, string userId)
    {
        var deck = await _context.Collections
            .AsNoTracking()
            .Where(c => c.Id == deckId && c.UserId == userId && c.IsDeck)
            .Include(c => c.Cards)
            .FirstOrDefaultAsync();

        if (deck == null)
            return null;

        var isPublished = await _context.ForumPosts.AnyAsync(p => p.DeckId == deckId);

        var cards = new List<CollectionCardDto>();
        foreach (var card in deck.Cards)
        {
            var cardDef = await _scryfallService.ResolveForEntryAsync(card);
            cards.Add(DomainMapper.ToDto(card, cardDef));
        }

        return new DeckDetailDto
        {
            Id = deck.Id,
            Name = deck.Name,
            CoverUri = deck.CoverUri,
            Format = deck.Format,
            CommanderOracleId = deck.CommanderOracleId,
            Tags = [.. deck.Tags],
            Notes = deck.Notes,
            IsPublished = isPublished,
            CreatedAt = deck.CreatedAt,
            UpdatedAt = deck.UpdatedAt,
            Cards = [.. cards]
        };
    }

    public async Task<DeckDetailDto> CreateDeckAsync(string userId, CreateDeckRequest request)
    {
        var deck = new Collection(userId, request.Name, isDeck: true, coverUri: request.CoverUri)
        {
            Format = request.Format,
            CommanderOracleId = request.CommanderOracleId,
        };
        _context.Collections.Add(deck);
        await _context.SaveChangesAsync();

        return new DeckDetailDto
        {
            Id = deck.Id,
            Name = deck.Name,
            CoverUri = deck.CoverUri,
            Format = deck.Format,
            CommanderOracleId = deck.CommanderOracleId,
            CreatedAt = deck.CreatedAt,
            UpdatedAt = deck.UpdatedAt,
            Cards = []
        };
    }

    public async Task<DeckDetailDto> UpdateDeckAsync(Guid deckId, string userId, UpdateDeckRequest request)
    {
        var deck = await _context.Collections
            .Where(c => c.Id == deckId && c.UserId == userId && c.IsDeck)
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("Deck not found");

        deck.Name = request.Name;
        deck.CoverUri = request.CoverUri;
        deck.Format = request.Format;
        deck.CommanderOracleId = request.CommanderOracleId;
        if (request.Tags is not null)
            // Normalize: the DTO caps the count; bound each tag's length here so one giant
            // tag can't slip past a per-element gap in model validation.
            deck.Tags = [.. request.Tags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim().Length > 60 ? t.Trim()[..60] : t.Trim())
                .Take(50)];
        if (request.Notes is not null)
            deck.Notes = request.Notes;
        deck.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return await GetDeckAsync(deckId, userId)
            ?? throw new InvalidOperationException("Failed to retrieve updated deck");
    }

    public async Task<bool> DeleteDeckAsync(Guid deckId, string userId)
    {
        var deck = await _context.Collections
            .Where(c => c.Id == deckId && c.UserId == userId && c.IsDeck)
            .FirstOrDefaultAsync();

        if (deck == null)
            return false;

        await RecordCardsLostWithCollectionAsync(deck);

        // A published deck's forum post references it by DeckId with no DB-level cascade,
        // so deleting the deck alone would strand a post that renders as "Unknown Deck" and
        // still accepts comments. Remove the post (and its comments) in the same operation.
        var postIds = await _context.ForumPosts
            .Where(p => p.DeckId == deckId)
            .Select(p => p.Id)
            .ToListAsync();
        if (postIds.Count > 0)
        {
            await _context.ForumComments
                .Where(c => postIds.Contains(c.ForumPostId))
                .ExecuteDeleteAsync();
            await _context.ForumPosts
                .Where(p => p.DeckId == deckId)
                .ExecuteDeleteAsync();
        }

        _context.Collections.Remove(deck);
        await _context.SaveChangesAsync();
        return true;
    }

}
