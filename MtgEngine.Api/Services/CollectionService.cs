using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Mapping;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public interface ICollectionService
{
    // Collections (IsDeck = false only)
    Task<CollectionDto[]> GetUserCollectionsAsync(string userId);
    Task<CollectionDetailDto?> GetCollectionAsync(Guid collectionId, string userId);
    Task<CollectionDetailDto> CreateCollectionAsync(string userId, CreateCollectionRequest request);
    Task<CollectionDetailDto> UpdateCollectionAsync(Guid collectionId, string userId, UpdateCollectionRequest request);
    Task<bool> DeleteCollectionAsync(Guid collectionId, string userId);

    // Shared card management (used by both collections and decks)
    Task<CollectionCardDto> AddCardToCollectionAsync(
        Guid collectionId,
        string userId,
        AddCardToCollectionRequest request);
    Task<CollectionCardDto?> GetCollectionCardAsync(Guid collectionId, Guid cardId, string userId);
    Task<CollectionCardDto> UpdateCollectionCardAsync(
        Guid collectionId,
        Guid cardId,
        string userId,
        UpdateCollectionCardRequest request);
    Task<bool> RemoveCardFromCollectionAsync(Guid collectionId, Guid cardId, string userId);
    Task<bool> RemoveCardByOracleAsync(Guid collectionId, string oracleId, string userId);

    // Deck building from collection
    Task<CardDto[]> GetAvailableCardsForDeckAsync(Guid collectionId, string userId);

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

    public CollectionService(MtgEngineDbContext context, ICardLookup scryfallService)
    {
        _context = context;
        _scryfallService = scryfallService;
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

        _context.Collections.Remove(collection);
        await _context.SaveChangesAsync();
        return true;
    }

    // ---- Collection Cards ----

    public async Task<CollectionCardDto> AddCardToCollectionAsync(
        Guid collectionId,
        string userId,
        AddCardToCollectionRequest request)
    {
        // Verify collection exists and belongs to user
        var collection = await _context.Collections
            .Where(c => c.Id == collectionId && c.UserId == userId)
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("Collection not found");

        var board = string.IsNullOrWhiteSpace(request.Board) ? "main" : request.Board;

        // Check if this exact printing+board already exists in the collection
        var existing = await _context.CollectionCards
            .Where(cc => cc.CollectionId == collectionId
                      && cc.OracleId == request.OracleId
                      && cc.ScryfallId == request.ScryfallId
                      && cc.Board == board)
            .FirstOrDefaultAsync();

        CollectionCard cardRecord;
        if (existing != null)
        {
            existing.Quantity += request.Quantity;
            existing.QuantityFoil += request.QuantityFoil;
            _context.CollectionCards.Update(existing);
            cardRecord = existing;
        }
        else
        {
            cardRecord = new CollectionCard(
                collectionId,
                request.OracleId,
                request.ScryfallId,
                request.Quantity,
                request.QuantityFoil,
                request.Notes,
                board);
            _context.CollectionCards.Add(cardRecord);
        }

        await _context.SaveChangesAsync();
        collection.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Pinned printing first — resolving by oracle id here made the added card render
        // with the default printing's art until the next full reload.
        var cardDef = await _scryfallService.ResolveForEntryAsync(cardRecord);
        return DomainMapper.ToDto(cardRecord, cardDef);
    }

    public async Task<CollectionCardDto?> GetCollectionCardAsync(Guid collectionId, Guid cardId, string userId)
    {
        var card = await _context.CollectionCards
            .AsNoTracking()
            .Where(cc => cc.Id == cardId && cc.CollectionId == collectionId)
            .FirstOrDefaultAsync();

        if (card == null)
            return null;

        // Verify collection belongs to user
        var collection = await _context.Collections
            .Where(c => c.Id == collectionId && c.UserId == userId)
            .FirstOrDefaultAsync();
        if (collection == null)
            return null;

        var cardDef = await _scryfallService.ResolveForEntryAsync(card);
        return DomainMapper.ToDto(card, cardDef);
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

        card.Quantity = request.Quantity;
        card.QuantityFoil = request.QuantityFoil;
        card.Notes = request.Notes;
        if (request.ScryfallId is not null)
            card.ScryfallId = request.ScryfallId;
        collection.UpdatedAt = DateTime.UtcNow;

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

        _context.CollectionCards.Remove(card);
        collection.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemoveCardByOracleAsync(Guid collectionId, string oracleId, string userId)
    {
        var collection = await _context.Collections
            .Where(c => c.Id == collectionId && c.UserId == userId)
            .FirstOrDefaultAsync();

        if (collection == null)
            return false;

        var card = await _context.CollectionCards
            .Where(cc => cc.CollectionId == collectionId && cc.OracleId == oracleId)
            .FirstOrDefaultAsync();

        if (card == null)
            return false;

        _context.CollectionCards.Remove(card);
        collection.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return true;
    }

    // ---- Deck Building ----

    public async Task<CardDto[]> GetAvailableCardsForDeckAsync(Guid collectionId, string userId)
    {
        var collection = await _context.Collections
            .AsNoTracking()
            .Where(c => c.Id == collectionId && c.UserId == userId)
            .Include(c => c.Cards)
            .FirstOrDefaultAsync();

        if (collection == null)
            return [];

        var cards = new List<CardDto>();
        foreach (var card in collection.Cards)
        {
            var cardDef = await _scryfallService.ResolveForEntryAsync(card);
            if (cardDef != null)
            {
                cards.Add(DomainMapper.ToDto(cardDef));
            }
        }

        return [.. cards];
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
            deck.Tags = [.. request.Tags];
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

        _context.Collections.Remove(deck);
        await _context.SaveChangesAsync();
        return true;
    }

}
