using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Models;
using MtgEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;

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
    private readonly IScryfallService _scryfallService;

    public CollectionService(MtgEngineDbContext context, IScryfallService scryfallService)
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
            var cardDef = card.ScryfallId is not null
                ? await _scryfallService.GetByScryfallIdAsync(card.ScryfallId)
                : await _scryfallService.GetByOracleIdAsync(card.OracleId);

            var dto = new CollectionCardDto
            {
                Id = card.Id,
                OracleId = card.OracleId,
                ScryfallId = card.ScryfallId,
                Quantity = card.Quantity,
                QuantityFoil = card.QuantityFoil,
                Notes = card.Notes,
                AddedAt = card.AddedAt,
                CardDetails = cardDef != null ? MapToCardDto(cardDef) : null
            };
            cards.Add(dto);
        }

        return new CollectionDetailDto
        {
            Id = collection.Id,
            Name = collection.Name,
            Description = collection.Description,
            CreatedAt = collection.CreatedAt,
            UpdatedAt = collection.UpdatedAt,
            Cards = [..cards]
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

        // Check if this exact printing already exists in the collection
        var existing = await _context.CollectionCards
            .Where(cc => cc.CollectionId == collectionId
                      && cc.OracleId == request.OracleId
                      && cc.ScryfallId == request.ScryfallId)
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
                request.Notes);
            _context.CollectionCards.Add(cardRecord);
        }

        await _context.SaveChangesAsync();
        collection.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Map to DTO with card details
        var cardDef = await _scryfallService.GetByOracleIdAsync(cardRecord.OracleId);
        return new CollectionCardDto
        {
            Id = cardRecord.Id,
            OracleId = cardRecord.OracleId,
            ScryfallId = cardRecord.ScryfallId,
            Quantity = cardRecord.Quantity,
            QuantityFoil = cardRecord.QuantityFoil,
            Notes = cardRecord.Notes,
            AddedAt = cardRecord.AddedAt,
            CardDetails = cardDef != null ? MapToCardDto(cardDef) : null
        };
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

        var cardDef = await _scryfallService.GetByOracleIdAsync(card.OracleId);
        return new CollectionCardDto
        {
            Id = card.Id,
            OracleId = card.OracleId,
            ScryfallId = card.ScryfallId,
            Quantity = card.Quantity,
            QuantityFoil = card.QuantityFoil,
            Notes = card.Notes,
            AddedAt = card.AddedAt,
            CardDetails = cardDef != null ? MapToCardDto(cardDef) : null
        };
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

        var cardDef = card.ScryfallId is not null
            ? await _scryfallService.GetByScryfallIdAsync(card.ScryfallId)
            : await _scryfallService.GetByOracleIdAsync(card.OracleId);
        return new CollectionCardDto
        {
            Id = card.Id,
            OracleId = card.OracleId,
            ScryfallId = card.ScryfallId,
            Quantity = card.Quantity,
            QuantityFoil = card.QuantityFoil,
            Notes = card.Notes,
            AddedAt = card.AddedAt,
            CardDetails = cardDef != null ? MapToCardDto(cardDef) : null
        };
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
            var cardDef = await _scryfallService.GetByOracleIdAsync(card.OracleId);
            if (cardDef != null)
            {
                cards.Add(MapToCardDto(cardDef));
            }
        }

        return [..cards];
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
                CoverUri = c.Description,
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

        if (deck == null) return null;

        var cards = new List<CollectionCardDto>();
        foreach (var card in deck.Cards)
        {
            var cardDef = card.ScryfallId is not null
                ? await _scryfallService.GetByScryfallIdAsync(card.ScryfallId)
                : await _scryfallService.GetByOracleIdAsync(card.OracleId);

            cards.Add(new CollectionCardDto
            {
                Id = card.Id,
                OracleId = card.OracleId,
                ScryfallId = card.ScryfallId,
                Quantity = card.Quantity,
                QuantityFoil = card.QuantityFoil,
                Notes = card.Notes,
                AddedAt = card.AddedAt,
                CardDetails = cardDef != null ? MapToCardDto(cardDef) : null
            });
        }

        return new DeckDetailDto
        {
            Id = deck.Id,
            Name = deck.Name,
            CoverUri = deck.Description,
            CreatedAt = deck.CreatedAt,
            UpdatedAt = deck.UpdatedAt,
            Cards = [..cards]
        };
    }

    public async Task<DeckDetailDto> CreateDeckAsync(string userId, CreateDeckRequest request)
    {
        var deck = new Collection(userId, request.Name, request.CoverUri, isDeck: true);
        _context.Collections.Add(deck);
        await _context.SaveChangesAsync();

        return new DeckDetailDto
        {
            Id = deck.Id,
            Name = deck.Name,
            CoverUri = deck.Description,
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
        deck.Description = request.CoverUri;
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

        if (deck == null) return false;

        _context.Collections.Remove(deck);
        await _context.SaveChangesAsync();
        return true;
    }

    // ---- Helpers ----

    private static CardDto MapToCardDto(CardDefinition def)
    {
        return new CardDto
        {
            CardId = def.OracleId,
            OracleId = def.OracleId,
            Name = def.Name,
            ManaCost = string.IsNullOrEmpty(def.ManaCostRaw) ? def.ManaCost.ToString() : def.ManaCostRaw,
            ManaValue = def.ManaCost.ManaValue,
            CardTypes = def.CardTypes.ToString().Split(", ")
                .Where(t => Enum.IsDefined(typeof(CardTypeDto), t))
                .Select(t => Enum.Parse<CardTypeDto>(t))
                .ToArray(),
            Subtypes = [..def.Subtypes],
            Supertypes = [..def.Supertypes],
            OracleText = def.OracleText,
            Power = def.Power,
            Toughness = def.Toughness,
            StartingLoyalty = def.StartingLoyalty,
            Keywords = def.Keywords.ToString().Split(", ")
                .Where(k => !string.IsNullOrEmpty(k) && k != "None")
                .ToArray(),
            ImageUriNormal     = def.ImageUriNormal,
            ImageUriNormalBack = def.ImageUriNormalBack,
            ImageUriSmall      = def.ImageUriSmall,
            ImageUriArtCrop    = def.ImageUriArtCrop,
            ColorIdentity = def.ColorIdentity
                .Select(c => c switch
                {
                    ManaColor.White => ManaColorDto.W,
                    ManaColor.Blue  => ManaColorDto.U,
                    ManaColor.Black => ManaColorDto.B,
                    ManaColor.Red   => ManaColorDto.R,
                    ManaColor.Green => ManaColorDto.G,
                    _               => ManaColorDto.C,
                })
                .ToArray(),
            FlavorText = def.FlavorText,
            Artist = def.Artist,
            SetCode = def.SetCode
        };
    }
}
