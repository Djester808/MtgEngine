using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;

namespace MtgEngine.Api.Controllers;

[ApiController]
[Route("api/users")]
public sealed class UsersController : ControllerBase
{
    private readonly MtgEngineDbContext _db;

    public UsersController(MtgEngineDbContext db) => _db = db;

    /// <summary>Lists all users who have published decks, with their stats.</summary>
    [HttpGet]
    public async Task<ActionResult<UserProfileDto[]>> GetPlayers()
    {
        var groups = await _db.ForumPosts
            .AsNoTracking()
            .GroupBy(p => p.AuthorUsername)
            .Select(g => new { Username = g.Key, DeckCount = g.Count(), Latest = g.Max(p => p.PublishedAt) })
            .OrderByDescending(g => g.DeckCount)
            .ToListAsync();

        var usernames = groups.Select(g => g.Username).ToList();

        var commentCounts = await _db.ForumComments
            .AsNoTracking()
            .Where(c => usernames.Contains(c.AuthorUsername))
            .GroupBy(c => c.AuthorUsername)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        var results = groups.Select(g => new UserProfileDto
        {
            Username = g.Username,
            JoinedAt = g.Latest,
            DeckCount = g.DeckCount,
            CommentCount = commentCounts.GetValueOrDefault(g.Username),
            PublishedDecks = [],
            RecentComments = [],
        }).ToArray();

        return Ok(results);
    }

    /// <summary>Public profile for a user: their published decks and recent comments.</summary>
    [HttpGet("{username}")]
    public async Task<ActionResult<UserProfileDto>> GetProfile(string username)
    {
        var posts = await _db.ForumPosts
            .AsNoTracking()
            .Where(p => EF.Functions.Like(p.AuthorUsername, username))
            .OrderByDescending(p => p.PublishedAt)
            .ToListAsync();

        var commentCount = await _db.ForumComments
            .AsNoTracking()
            .CountAsync(c => EF.Functions.Like(c.AuthorUsername, username));

        if (!posts.Any() && commentCount == 0)
            return NotFound(new { message = $"User '{username}' not found." });

        // Resolve actual username casing from first result
        var resolvedUsername = posts.Any() ? posts[0].AuthorUsername : username;

        var deckIds = posts.Select(p => p.DeckId).Distinct().ToList();

        var decks = await _db.Collections
            .AsNoTracking()
            .Where(c => deckIds.Contains(c.Id) && c.IsDeck)
            .Select(c => new { c.Id, c.Name, c.CoverUri, c.Format })
            .ToDictionaryAsync(c => c.Id);

        var cardCounts = await _db.Collections
            .AsNoTracking()
            .Where(c => deckIds.Contains(c.Id) && c.IsDeck)
            .Select(c => new { c.Id, Count = c.Cards.Sum(cc => cc.Quantity + cc.QuantityFoil) })
            .ToDictionaryAsync(c => c.Id, c => c.Count);

        var postIds = posts.Select(p => p.Id).ToList();
        var commentCounts = await _db.ForumComments
            .AsNoTracking()
            .Where(fc => postIds.Contains(fc.ForumPostId))
            .GroupBy(fc => fc.ForumPostId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        var publishedDecks = posts.Select(p =>
        {
            decks.TryGetValue(p.DeckId, out var deck);
            var colors = JsonSerializer.Deserialize<string[]>(p.ColorIdentityJson) ?? [];
            return new ForumPostSummaryDto
            {
                Id = p.Id,
                DeckId = p.DeckId,
                AuthorUsername = p.AuthorUsername,
                DeckName = deck?.Name ?? "Unknown Deck",
                DeckCoverUri = deck?.CoverUri,
                DeckFormat = deck?.Format,
                Description = p.Description,
                ColorIdentity = colors,
                CardCount = cardCounts.GetValueOrDefault(p.DeckId),
                CommentCount = commentCounts.GetValueOrDefault(p.Id),
                PublishedAt = p.PublishedAt,
            };
        }).ToArray();

        var comments = await _db.ForumComments
            .AsNoTracking()
            .Where(c => EF.Functions.Like(c.AuthorUsername, username))
            .OrderByDescending(c => c.CreatedAt)
            .Take(20)
            .ToListAsync();

        var commentPostIds = comments.Select(c => c.ForumPostId).Distinct().ToList();
        var commentPosts = await _db.ForumPosts
            .AsNoTracking()
            .Where(p => commentPostIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.DeckId);

        var commentDeckIds = commentPosts.Values.Distinct().ToList();
        var commentDecks = await _db.Collections
            .AsNoTracking()
            .Where(c => commentDeckIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);

        var recentComments = comments.Select(c =>
        {
            commentPosts.TryGetValue(c.ForumPostId, out var deckId);
            commentDecks.TryGetValue(deckId, out var deckName);
            return new UserCommentDto
            {
                CommentId = c.Id,
                ForumPostId = c.ForumPostId,
                DeckName = deckName ?? "Unknown Deck",
                Content = c.Content,
                CreatedAt = c.CreatedAt,
            };
        }).ToArray();

        var joinedAt = posts.Any() ? posts.Min(p => p.PublishedAt)
            : comments.Any() ? comments.Min(c => c.CreatedAt) : DateTime.UtcNow;

        return Ok(new UserProfileDto
        {
            Username = resolvedUsername,
            JoinedAt = joinedAt,
            DeckCount = posts.Count,
            CommentCount = commentCount,
            PublishedDecks = publishedDecks,
            RecentComments = recentComments,
        });
    }
}
