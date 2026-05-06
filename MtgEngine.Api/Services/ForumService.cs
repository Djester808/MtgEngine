using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public interface IForumService
{
    Task<ForumPostSummaryDto[]> GetAllPostsAsync();
    Task<ForumPostDetailDto?> GetPostAsync(Guid postId);
    Task<ForumPostSummaryDto> PublishDeckAsync(string userId, string username, PublishDeckRequest request);
    Task<bool> DeletePostAsync(Guid postId, string userId);
    Task<ForumCommentDto> AddCommentAsync(Guid postId, string userId, string username, CreateCommentRequest request);
    Task<ForumCommentDto?> UpdateCommentAsync(Guid postId, Guid commentId, string userId, UpdateCommentRequest request);
    Task<bool> DeleteCommentAsync(Guid postId, Guid commentId, string userId);
}

public sealed class ForumService : IForumService
{
    private readonly MtgEngineDbContext _context;
    private readonly IScryfallService _scryfall;
    private readonly ICollectionService _collections;

    private static readonly string[] ColorOrder = ["W", "U", "B", "R", "G"];

    public ForumService(MtgEngineDbContext context, IScryfallService scryfall, ICollectionService collections)
    {
        _context = context;
        _scryfall = scryfall;
        _collections = collections;
    }

    public async Task<ForumPostSummaryDto[]> GetAllPostsAsync()
    {
        var posts = await _context.ForumPosts
            .AsNoTracking()
            .OrderByDescending(p => p.PublishedAt)
            .ToListAsync();

        var deckIds = posts.Select(p => p.DeckId).Distinct().ToList();
        var decks = await _context.Collections
            .AsNoTracking()
            .Where(c => deckIds.Contains(c.Id) && c.IsDeck)
            .Select(c => new { c.Id, c.Name, c.Description, c.Format, c.Cards })
            .ToListAsync();

        var deckCardCounts = await _context.Collections
            .AsNoTracking()
            .Where(c => deckIds.Contains(c.Id) && c.IsDeck)
            .Select(c => new { c.Id, CardCount = c.Cards.Sum(cc => cc.Quantity + cc.QuantityFoil) })
            .ToDictionaryAsync(x => x.Id, x => x.CardCount);

        var commentCounts = await _context.ForumComments
            .AsNoTracking()
            .Where(fc => posts.Select(p => p.Id).Contains(fc.ForumPostId))
            .GroupBy(fc => fc.ForumPostId)
            .Select(g => new { PostId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PostId, x => x.Count);

        var deckMap = decks.ToDictionary(d => d.Id);

        return posts.Select(p =>
        {
            deckMap.TryGetValue(p.DeckId, out var deck);
            var colorIdentity = JsonSerializer.Deserialize<string[]>(p.ColorIdentityJson, (JsonSerializerOptions?)null) ?? [];
            return new ForumPostSummaryDto
            {
                Id = p.Id,
                DeckId = p.DeckId,
                AuthorUsername = p.AuthorUsername,
                DeckName = deck?.Name ?? "Unknown Deck",
                DeckCoverUri = deck?.Description,
                DeckFormat = deck?.Format,
                Description = p.Description,
                ColorIdentity = colorIdentity,
                CardCount = deckCardCounts.GetValueOrDefault(p.DeckId),
                CommentCount = commentCounts.GetValueOrDefault(p.Id),
                PublishedAt = p.PublishedAt,
            };
        }).ToArray();
    }

    public async Task<ForumPostDetailDto?> GetPostAsync(Guid postId)
    {
        var post = await _context.ForumPosts
            .AsNoTracking()
            .Include(p => p.Comments)
            .FirstOrDefaultAsync(p => p.Id == postId);

        if (post == null) return null;

        var deck = await _context.Collections
            .AsNoTracking()
            .Where(c => c.Id == post.DeckId && c.IsDeck)
            .Include(c => c.Cards)
            .FirstOrDefaultAsync();

        CollectionCardDto[] cardDtos = [];
        if (deck != null)
        {
            var cardList = new List<CollectionCardDto>();
            foreach (var card in deck.Cards)
            {
                var cardDef = card.ScryfallId is not null
                    ? await _scryfall.GetByScryfallIdAsync(card.ScryfallId)
                    : await _scryfall.GetByOracleIdAsync(card.OracleId);

                cardList.Add(new CollectionCardDto
                {
                    Id = card.Id,
                    OracleId = card.OracleId,
                    ScryfallId = card.ScryfallId,
                    Quantity = card.Quantity,
                    QuantityFoil = card.QuantityFoil,
                    Notes = card.Notes,
                    Board = card.Board is "main" or "side" or "maybe" ? card.Board : "main",
                    AddedAt = card.AddedAt,
                    CardDetails = cardDef != null ? MapToCardDto(cardDef) : null,
                });
            }
            cardDtos = [..cardList];
        }

        var colorIdentity = JsonSerializer.Deserialize<string[]>(post.ColorIdentityJson, (JsonSerializerOptions?)null) ?? [];

        return new ForumPostDetailDto
        {
            Id = post.Id,
            DeckId = post.DeckId,
            AuthorId = post.AuthorId,
            AuthorUsername = post.AuthorUsername,
            DeckName = deck?.Name ?? "Unknown Deck",
            DeckCoverUri = deck?.Description,
            DeckFormat = deck?.Format,
            CommanderOracleId = deck?.CommanderOracleId,
            Description = post.Description,
            ColorIdentity = colorIdentity,
            PublishedAt = post.PublishedAt,
            UpdatedAt = post.UpdatedAt,
            Cards = cardDtos,
            Comments = post.Comments
                .OrderBy(c => c.CreatedAt)
                .Select(c => new ForumCommentDto
                {
                    Id = c.Id,
                    AuthorId = c.AuthorId,
                    AuthorUsername = c.AuthorUsername,
                    Content = c.Content,
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = c.UpdatedAt,
                })
                .ToArray(),
        };
    }

    public async Task<ForumPostSummaryDto> PublishDeckAsync(string userId, string username, PublishDeckRequest request)
    {
        var deck = await _context.Collections
            .AsNoTracking()
            .Where(c => c.Id == request.DeckId && c.UserId == userId && c.IsDeck)
            .Include(c => c.Cards)
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("Deck not found");

        // Compute color identity from all cards
        var colorSet = new HashSet<string>();
        foreach (var card in deck.Cards)
        {
            var cardDef = await _scryfall.GetByOracleIdAsync(card.OracleId);
            if (cardDef == null) continue;
            foreach (var color in cardDef.ColorIdentity)
            {
                var letter = color switch
                {
                    ManaColor.White => "W",
                    ManaColor.Blue  => "U",
                    ManaColor.Black => "B",
                    ManaColor.Red   => "R",
                    ManaColor.Green => "G",
                    _               => null,
                };
                if (letter != null) colorSet.Add(letter);
            }
        }
        var colorIdentity = ColorOrder.Where(colorSet.Contains).ToArray();
        var colorJson = JsonSerializer.Serialize(colorIdentity);

        // Upsert: update description if post already exists for this deck
        var existing = await _context.ForumPosts
            .FirstOrDefaultAsync(p => p.DeckId == request.DeckId);

        ForumPost post;
        if (existing != null)
        {
            existing.Description = request.Description;
            existing.ColorIdentityJson = colorJson;
            existing.UpdatedAt = DateTime.UtcNow;
            post = existing;
        }
        else
        {
            post = new ForumPost
            {
                DeckId = request.DeckId,
                AuthorId = userId,
                AuthorUsername = username,
                Description = request.Description,
                ColorIdentityJson = colorJson,
            };
            _context.ForumPosts.Add(post);
        }

        await _context.SaveChangesAsync();

        var cardCount = deck.Cards.Sum(c => c.Quantity + c.QuantityFoil);

        return new ForumPostSummaryDto
        {
            Id = post.Id,
            DeckId = post.DeckId,
            AuthorUsername = post.AuthorUsername,
            DeckName = deck.Name,
            DeckCoverUri = deck.Description,
            DeckFormat = deck.Format,
            Description = post.Description,
            ColorIdentity = colorIdentity,
            CardCount = cardCount,
            CommentCount = 0,
            PublishedAt = post.PublishedAt,
        };
    }

    public async Task<bool> DeletePostAsync(Guid postId, string userId)
    {
        var post = await _context.ForumPosts
            .FirstOrDefaultAsync(p => p.Id == postId && p.AuthorId == userId);

        if (post == null) return false;

        _context.ForumPosts.Remove(post);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<ForumCommentDto> AddCommentAsync(Guid postId, string userId, string username, CreateCommentRequest request)
    {
        var postExists = await _context.ForumPosts.AnyAsync(p => p.Id == postId);
        if (!postExists) throw new KeyNotFoundException("Forum post not found");

        var comment = new ForumComment
        {
            ForumPostId = postId,
            AuthorId = userId,
            AuthorUsername = username,
            Content = request.Content,
        };

        _context.ForumComments.Add(comment);
        await _context.SaveChangesAsync();

        return new ForumCommentDto
        {
            Id = comment.Id,
            AuthorId = comment.AuthorId,
            AuthorUsername = comment.AuthorUsername,
            Content = comment.Content,
            CreatedAt = comment.CreatedAt,
            UpdatedAt = comment.UpdatedAt,
        };
    }

    public async Task<ForumCommentDto?> UpdateCommentAsync(Guid postId, Guid commentId, string userId, UpdateCommentRequest request)
    {
        var comment = await _context.ForumComments
            .FirstOrDefaultAsync(c => c.Id == commentId && c.ForumPostId == postId && c.AuthorId == userId);

        if (comment == null) return null;

        comment.Content = request.Content;
        comment.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return new ForumCommentDto
        {
            Id = comment.Id,
            AuthorId = comment.AuthorId,
            AuthorUsername = comment.AuthorUsername,
            Content = comment.Content,
            CreatedAt = comment.CreatedAt,
            UpdatedAt = comment.UpdatedAt,
        };
    }

    public async Task<bool> DeleteCommentAsync(Guid postId, Guid commentId, string userId)
    {
        var comment = await _context.ForumComments
            .FirstOrDefaultAsync(c => c.Id == commentId && c.ForumPostId == postId && c.AuthorId == userId);

        if (comment == null) return false;

        _context.ForumComments.Remove(comment);
        await _context.SaveChangesAsync();
        return true;
    }

    private static CardDto MapToCardDto(CardDefinition def)
    {
        return new CardDto
        {
            CardId = def.OracleId,
            OracleId = def.OracleId,
            Name = def.Name,
            ManaCost = string.IsNullOrEmpty(def.ManaCostRaw) ? def.ManaCost.ToString() : def.ManaCostRaw,
            ManaValue = def.Cmc,
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
            SetCode = def.SetCode,
            Rarity = def.Rarity,
            Legalities = def.Legalities.ToDictionary(kv => kv.Key, kv => kv.Value),
            GameChanger = def.GameChanger,
        };
    }
}
