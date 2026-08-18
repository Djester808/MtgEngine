using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Mapping;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public interface IForumService
{
    Task<ForumPostSummaryDto[]> GetAllPostsAsync();

    /// <summary>Every post a user has published, newest first. Backs their profile.</summary>
    Task<ForumPostSummaryDto[]> GetPostsByAuthorAsync(string authorId, CancellationToken ct = default);

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
    private readonly ICardLookup _scryfall;
    private readonly ICollectionService _collections;

    private static readonly string[] ColorOrder = ["W", "U", "B", "R", "G"];

    public ForumService(MtgEngineDbContext context, ICardLookup scryfall, ICollectionService collections)
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

        return await SummariseAsync(posts, CancellationToken.None);
    }

    public async Task<ForumPostSummaryDto[]> GetPostsByAuthorAsync(string authorId, CancellationToken ct = default)
    {
        var posts = await _context.ForumPosts
            .AsNoTracking()
            .Where(p => p.AuthorId == authorId)
            .OrderByDescending(p => p.PublishedAt)
            .ToListAsync(ct);

        return await SummariseAsync(posts, ct);
    }

    /// <summary>
    /// Turns posts into summaries, resolving the deck name, cover, card count and comment
    /// count each one needs.
    /// </summary>
    /// <remarks>
    /// Shared by the forum list and the profile rather than written twice. It had already
    /// been written twice — <c>UsersController</c> carried its own copy, which is how the
    /// profile ended up showing a card count that summed only non-foil copies.
    /// </remarks>
    private async Task<ForumPostSummaryDto[]> SummariseAsync(List<ForumPost> posts, CancellationToken ct)
    {
        if (posts.Count == 0)
            return [];

        var deckIds = posts.Select(p => p.DeckId).Distinct().ToList();
        var decks = await _context.Collections
            .AsNoTracking()
            .Where(c => deckIds.Contains(c.Id) && c.IsDeck)
            .Select(c => new { c.Id, c.Name, c.CoverUri, c.Description, c.Format, c.Cards })
            .ToListAsync(ct);

        var deckCardCounts = await _context.Collections
            .AsNoTracking()
            .Where(c => deckIds.Contains(c.Id) && c.IsDeck)
            .Select(c => new { c.Id, CardCount = c.Cards.Sum(cc => cc.Quantity + cc.QuantityFoil) })
            .ToDictionaryAsync(x => x.Id, x => x.CardCount, ct);

        var postIds = posts.Select(p => p.Id).ToList();
        var commentCounts = await _context.ForumComments
            .AsNoTracking()
            .Where(fc => postIds.Contains(fc.ForumPostId))
            .GroupBy(fc => fc.ForumPostId)
            .Select(g => new { PostId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PostId, x => x.Count, ct);

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
                DeckCoverUri = deck?.CoverUri,
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

        if (post == null)
            return null;

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
                var cardDef = await _scryfall.ResolveForEntryAsync(card);
                cardList.Add(DomainMapper.ToDto(card, cardDef));
            }
            cardDtos = [.. cardList];
        }

        var colorIdentity = JsonSerializer.Deserialize<string[]>(post.ColorIdentityJson, (JsonSerializerOptions?)null) ?? [];

        string? commanderImageUri = null;
        string? commanderName = null;
        if (deck?.CommanderOracleId is not null)
        {
            var cmdDef = await _scryfall.GetByOracleIdAsync(deck.CommanderOracleId);
            commanderImageUri = cmdDef?.ImageUriNormal;
            commanderName = cmdDef?.Name;
        }

        return new ForumPostDetailDto
        {
            Id = post.Id,
            DeckId = post.DeckId,
            AuthorId = post.AuthorId,
            AuthorUsername = post.AuthorUsername,
            DeckName = deck?.Name ?? "Unknown Deck",
            DeckCoverUri = deck?.CoverUri,
            DeckFormat = deck?.Format,
            CommanderOracleId = deck?.CommanderOracleId,
            CommanderImageUri = commanderImageUri,
            CommanderName = commanderName,
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
            if (cardDef == null)
                continue;
            foreach (var color in cardDef.ColorIdentity)
            {
                var letter = color switch
                {
                    ManaColor.White => "W",
                    ManaColor.Blue => "U",
                    ManaColor.Black => "B",
                    ManaColor.Red => "R",
                    ManaColor.Green => "G",
                    _ => null,
                };
                if (letter != null)
                    colorSet.Add(letter);
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
            DeckCoverUri = deck.CoverUri,
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

        if (post == null)
            return false;

        _context.ForumPosts.Remove(post);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<ForumCommentDto> AddCommentAsync(Guid postId, string userId, string username, CreateCommentRequest request)
    {
        var postExists = await _context.ForumPosts.AnyAsync(p => p.Id == postId);
        if (!postExists)
            throw new KeyNotFoundException("Forum post not found");

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

        if (comment == null)
            return null;

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

        if (comment == null)
            return false;

        _context.ForumComments.Remove(comment);
        await _context.SaveChangesAsync();
        return true;
    }

}
