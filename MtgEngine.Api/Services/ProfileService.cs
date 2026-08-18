using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public interface IProfileService
{
    Task<PlayerSummaryDto[]> GetPlayersAsync(int limit, CancellationToken ct = default);

    Task<UserProfileDto> GetPublicProfileAsync(string username, CancellationToken ct = default);

    Task<UserCommentPageDto> GetCommentHistoryAsync(
        string username, int page, int pageSize, CancellationToken ct = default);

    Task<MyProfileDto> GetMyProfileAsync(Guid userId, CancellationToken ct = default);

    Task<MyProfileDto> UpdateMyProfileAsync(
        Guid userId, UpdateProfileRequest request, CancellationToken ct = default);

    /// <summary>Validates and stores an avatar. Throws <see cref="InvalidRequestException"/> if it is not a usable image.</summary>
    Task<MyProfileDto> SetAvatarAsync(Guid userId, byte[] bytes, CancellationToken ct = default);

    Task<MyProfileDto> DeleteAvatarAsync(Guid userId, CancellationToken ct = default);

    Task<AvatarContentDto?> GetAvatarAsync(string username, CancellationToken ct = default);
}

/// <summary>
/// Builds user profiles: the self-authored part (name, bio, avatar) and the earned part
/// (stats derived from decks, collections and forum activity).
/// </summary>
/// <remarks>
/// This replaces the projection <c>UsersController</c> used to build inline. Two things
/// changed with the move, beyond the controller getting thin:
/// <list type="bullet">
/// <item>A profile is keyed on the <see cref="User"/> row, not on forum authorship. The old
/// version derived the whole thing from <c>ForumPosts</c>, so a member who had never
/// published got a 404 on their own profile and a join date taken from their first post.</item>
/// <item>Public and private are separated deliberately. Counts are public; what a
/// collection is <em>worth</em> is not, and lives in <see cref="PrivateStatsDto"/> behind
/// the owner-only endpoint.</item>
/// </list>
/// </remarks>
public sealed class ProfileService : IProfileService
{
    private readonly MtgEngineDbContext _db;
    private readonly ICardLookup _cards;
    private readonly IForumService _forum;

    /// <summary>WUBRG. Colour spread is reported in this order, not in count order.</summary>
    private static readonly string[] ColorOrder = ["W", "U", "B", "R", "G"];

    /// <summary>Rails are short: this is a phone-first payload, not a report.</summary>
    private const int RailSize = 6;

    /// <summary>Comment history embedded in the profile. More arrives through the paged endpoint.</summary>
    public const int EmbeddedCommentCount = 10;

    /// <summary>Largest page the comment-history endpoint will serve, whatever is asked for.</summary>
    public const int MaxCommentPageSize = 50;

    public const int DefaultCommentPageSize = 20;

    /// <summary>Largest player list served, whatever is asked for.</summary>
    public const int MaxPlayerLimit = 200;

    /// <summary>
    /// Ceiling on collection rows priced for the owner's value figure. A pathological
    /// collection must not turn one profile load into an unbounded scan; past this the
    /// total stops growing and <c>CopiesValued</c> shows it was truncated.
    /// </summary>
    private const int MaxRowsValued = 20_000;

    public ProfileService(MtgEngineDbContext db, ICardLookup cards, IForumService forum)
    {
        _db = db;
        _cards = cards;
        _forum = forum;
    }

    // ---- Read: public ------------------------------------------------------

    public async Task<PlayerSummaryDto[]> GetPlayersAsync(int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, MaxPlayerLimit);

        // Ordered by contribution, so the list opens on the people worth reading.
        var users = await _db.Users
            .AsNoTracking()
            .Select(u => new
            {
                u.Id,
                u.Username,
                u.DisplayName,
                u.Tagline,
                u.CreatedAt,
                u.AvatarUpdatedAt,
            })
            .ToListAsync(ct);

        var ids = users.Select(u => u.Id.ToString()).ToList();

        var deckCounts = await _db.ForumPosts
            .AsNoTracking()
            .Where(p => ids.Contains(p.AuthorId))
            .GroupBy(p => p.AuthorId)
            .Select(g => new { AuthorId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AuthorId, x => x.Count, ct);

        var commentCounts = await _db.ForumComments
            .AsNoTracking()
            .Where(c => ids.Contains(c.AuthorId))
            .GroupBy(c => c.AuthorId)
            .Select(g => new { AuthorId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AuthorId, x => x.Count, ct);

        return [.. users
            .Select(u =>
            {
                var key = u.Id.ToString();
                return new PlayerSummaryDto
                {
                    Username = u.Username,
                    DisplayName = u.DisplayName,
                    Tagline = u.Tagline,
                    AvatarUrl = AvatarUrl(u.Username, u.AvatarUpdatedAt),
                    JoinedAt = u.CreatedAt,
                    DeckCount = deckCounts.GetValueOrDefault(key),
                    CommentCount = commentCounts.GetValueOrDefault(key),
                };
            })
            .OrderByDescending(p => p.DeckCount)
            .ThenByDescending(p => p.CommentCount)
            .ThenBy(p => p.Username, StringComparer.OrdinalIgnoreCase)
            .Take(take)];
    }

    public async Task<UserProfileDto> GetPublicProfileAsync(string username, CancellationToken ct = default)
    {
        var user = await FindUserAsync(username, ct);
        return await BuildProfileAsync(user, includePrivateDecks: false, ct);
    }

    public async Task<UserCommentPageDto> GetCommentHistoryAsync(
        string username, int page, int pageSize, CancellationToken ct = default)
    {
        var user = await FindUserAsync(username, ct);
        var authorId = user.Id.ToString();

        var size = Math.Clamp(pageSize, 1, MaxCommentPageSize);
        var pageIndex = Math.Max(page, 1);

        var query = _db.ForumComments.AsNoTracking().Where(c => c.AuthorId == authorId);

        var total = await query.CountAsync(ct);

        var comments = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((pageIndex - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        return new UserCommentPageDto
        {
            Total = total,
            Page = pageIndex,
            PageSize = size,
            Items = await ToCommentDtosAsync(comments, ct),
        };
    }

    // ---- Read: owner -------------------------------------------------------

    public async Task<MyProfileDto> GetMyProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await FindUserAsync(userId, ct);
        return await BuildMyProfileAsync(user, ct);
    }

    // ---- Write -------------------------------------------------------------

    public async Task<MyProfileDto> UpdateMyProfileAsync(
        Guid userId, UpdateProfileRequest request, CancellationToken ct = default)
    {
        var user = await FindUserAsync(userId, ct);

        // Whitespace-only input is nothing, and storing it would render as an empty line
        // where the client expects either text or a placeholder.
        user.DisplayName = Clean(request.DisplayName);
        user.Tagline = Clean(request.Tagline);
        user.Bio = Clean(request.Bio);
        user.FavoriteFormat = Clean(request.FavoriteFormat);
        user.FavoriteCommanderOracleId = await ValidateCommanderAsync(
            Clean(request.FavoriteCommanderOracleId), ct);
        user.ProfileUpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return await BuildMyProfileAsync(user, ct);
    }

    public async Task<MyProfileDto> SetAvatarAsync(Guid userId, byte[] bytes, CancellationToken ct = default)
    {
        var user = await FindUserAsync(userId, ct);

        // The declared content type never reaches this; the format is read out of the bytes.
        if (!AvatarImage.TryValidate(bytes, out var image, out var error))
            throw new InvalidRequestException(error);

        var avatar = await _db.Set<UserAvatar>().FirstOrDefaultAsync(a => a.UserId == userId, ct);
        if (avatar is null)
        {
            avatar = new UserAvatar { UserId = userId };
            _db.Add(avatar);
        }

        avatar.Data = bytes;
        avatar.ContentType = image.ContentType;
        avatar.Width = image.Width;
        avatar.Height = image.Height;
        avatar.ETag = image.ETag;
        avatar.UpdatedAt = DateTime.UtcNow;

        // Mirrored onto the user row so profile reads never touch the blob table.
        user.AvatarUpdatedAt = avatar.UpdatedAt;

        await _db.SaveChangesAsync(ct);

        return await BuildMyProfileAsync(user, ct);
    }

    public async Task<MyProfileDto> DeleteAvatarAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await FindUserAsync(userId, ct);

        await _db.Set<UserAvatar>().Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);

        user.AvatarUpdatedAt = null;
        await _db.SaveChangesAsync(ct);

        return await BuildMyProfileAsync(user, ct);
    }

    public async Task<AvatarContentDto?> GetAvatarAsync(string username, CancellationToken ct = default)
    {
        var normalized = username.ToLower();

        var row = await _db.Users
            .AsNoTracking()
            .Where(u => u.Username.ToLower() == normalized)
            .Join(
                _db.Set<UserAvatar>().AsNoTracking(),
                u => u.Id,
                a => a.UserId,
                (u, a) => new { a.Data, a.ContentType, a.ETag, a.UpdatedAt })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? null
            : new AvatarContentDto(row.Data, row.ContentType, row.ETag, row.UpdatedAt);
    }

    // ---- Composition -------------------------------------------------------

    private async Task<MyProfileDto> BuildMyProfileAsync(User user, CancellationToken ct)
    {
        var profile = await BuildProfileAsync(user, includePrivateDecks: true, ct);
        return new MyProfileDto
        {
            Profile = profile,
            Email = user.Email,
            PrivateStats = await BuildPrivateStatsAsync(user, profile.Stats, ct),
        };
    }

    private async Task<UserProfileDto> BuildProfileAsync(User user, bool includePrivateDecks, CancellationToken ct)
    {
        var userId = user.Id.ToString();

        // One pass over everything the user owns. Decks and collections share this table,
        // so IsDeck is what separates "decks I built" from "cards I own".
        var owned = await _db.Collections
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => new OwnedCollection(
                c.Id,
                c.Name,
                c.CoverUri,
                c.Format,
                c.CommanderOracleId,
                c.IsDeck,
                c.UpdatedAt,
                c.Cards.Sum(cc => cc.Quantity + cc.QuantityFoil)))
            .ToListAsync(ct);

        ct.ThrowIfCancellationRequested();

        var decks = owned.Where(c => c.IsDeck).ToList();
        var collections = owned.Where(c => !c.IsDeck).ToList();

        var published = await _forum.GetPostsByAuthorAsync(userId, ct);
        var publishedByDeck = published
            .GroupBy(p => p.DeckId)
            .ToDictionary(g => g.Key, g => g.First());

        var commentsPosted = await _db.ForumComments
            .AsNoTracking()
            .CountAsync(c => c.AuthorId == userId, ct);

        // Comments *others* left on their decks. Counting their own replies would let
        // anyone inflate the number by talking to themselves.
        var postIds = published.Select(p => p.Id).ToList();
        var commentsReceived = postIds.Count == 0
            ? 0
            : await _db.ForumComments
                .AsNoTracking()
                .CountAsync(c => postIds.Contains(c.ForumPostId) && c.AuthorId != userId, ct);

        var distinctCards = await _db.CollectionCards
            .AsNoTracking()
            .Where(cc => cc.Collection.UserId == userId
                         && !cc.Collection.IsDeck
                         && cc.Quantity + cc.QuantityFoil > 0)
            .Select(cc => cc.OracleId)
            .Distinct()
            .CountAsync(ct);

        ct.ThrowIfCancellationRequested();

        var recentComments = await ToCommentDtosAsync(
            await _db.ForumComments
                .AsNoTracking()
                .Where(c => c.AuthorId == userId)
                .OrderByDescending(c => c.CreatedAt)
                .Take(EmbeddedCommentCount)
                .ToListAsync(ct),
            ct);

        var commanderCounts = decks
            .Where(d => !string.IsNullOrEmpty(d.CommanderOracleId))
            .GroupBy(d => d.CommanderOracleId!)
            .Select(g => (OracleId: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.OracleId, StringComparer.Ordinal)
            .ToList();

        var topCommanders = await ResolveCommandersAsync(commanderCounts.Take(RailSize), ct);

        // The colour spread describes decks whose commander resolves â€” the deck's own
        // colour identity is not stored, and deriving it from every card in every deck
        // would cost far more than this stat is worth.
        var colorSpread = await BuildColorSpreadAsync(commanderCounts, ct);

        var favoriteCommander = await ResolveFavoriteCommanderAsync(
            user.FavoriteCommanderOracleId, commanderCounts, ct);

        // "Most engaged": their published decks, best conversation first.
        var topDecks = published
            .OrderByDescending(p => p.CommentCount)
            .ThenByDescending(p => p.PublishedAt)
            .Take(RailSize)
            .ToArray();

        // "Most edited". A public profile only ever lists published decks here â€” an
        // unpublished deck is private work, and its name is not the visitor's business.
        var activeSource = includePrivateDecks
            ? decks
            : decks.Where(d => publishedByDeck.ContainsKey(d.Id));

        var recentlyActive = activeSource
            .OrderByDescending(d => d.UpdatedAt)
            .Take(RailSize)
            .Select(d => new ActiveDeckDto
            {
                DeckId = d.Id,
                Name = d.Name,
                CoverUri = d.CoverUri,
                Format = d.Format,
                CardCount = d.CardCount,
                UpdatedAt = d.UpdatedAt,
                ForumPostId = publishedByDeck.TryGetValue(d.Id, out var post) ? post.Id : null,
            })
            .ToArray();

        var stats = new ProfileStatsDto
        {
            DecksBuilt = decks.Count,
            DecksPublished = published.Length,
            Collections = collections.Count,
            CardsOwned = collections.Sum(c => c.CardCount),
            DistinctCards = distinctCards,
            CommentsPosted = commentsPosted,
            CommentsReceived = commentsReceived,
            ColorSpread = colorSpread,
            Formats = [.. decks
                .Where(d => !string.IsNullOrWhiteSpace(d.Format))
                .GroupBy(d => d.Format!, StringComparer.OrdinalIgnoreCase)
                .Select(g => new FormatCountDto(g.Key, g.Count()))
                .OrderByDescending(f => f.DeckCount)
                .ThenBy(f => f.Format, StringComparer.OrdinalIgnoreCase)],
            LastActiveAt = owned.Count == 0 ? null : owned.Max(c => c.UpdatedAt),
        };

        return new UserProfileDto
        {
            Username = user.Username,
            DisplayName = user.DisplayName,
            Tagline = user.Tagline,
            Bio = user.Bio,
            FavoriteFormat = user.FavoriteFormat,
            AvatarUrl = AvatarUrl(user.Username, user.AvatarUpdatedAt),
            JoinedAt = user.CreatedAt,
            DeckCount = published.Length,
            CommentCount = commentsPosted,
            Stats = stats,
            FavoriteCommander = favoriteCommander,
            TopCommanders = topCommanders,
            MostPlayedCards = await GetMostPlayedCardsAsync(userId, ct),
            TopDecks = topDecks,
            RecentlyActive = recentlyActive,
            PublishedDecks = published,
            RecentComments = recentComments,
        };
    }

    private async Task<PrivateStatsDto> BuildPrivateStatsAsync(
        User user, ProfileStatsDto stats, CancellationToken ct)
    {
        var userId = user.Id.ToString();

        var rows = await _db.CollectionCards
            .AsNoTracking()
            .Where(cc => cc.Collection.UserId == userId
                         && !cc.Collection.IsDeck
                         && cc.Quantity + cc.QuantityFoil > 0)
            .Select(cc => new { cc.OracleId, cc.ScryfallId, cc.Quantity, cc.QuantityFoil })
            .Take(MaxRowsValued)
            .ToListAsync(ct);

        decimal total = 0;
        var copiesValued = 0;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var card = row.ScryfallId is not null
                ? await _cards.GetByScryfallIdAsync(row.ScryfallId) ?? await _cards.GetByOracleIdAsync(row.OracleId)
                : await _cards.GetByOracleIdAsync(row.OracleId);

            if (card is null)
                continue;

            // A foil copy is worth the foil price; falling back to the non-foil price
            // would understate a collection whose value is mostly in its foils.
            if (row.Quantity > 0 && card.Prices.Usd is { } usd)
            {
                total += usd * row.Quantity;
                copiesValued += row.Quantity;
            }

            var foilPrice = card.Prices.UsdFoil ?? card.Prices.UsdEtched;
            if (row.QuantityFoil > 0 && foilPrice is { } foil)
            {
                total += foil * row.QuantityFoil;
                copiesValued += row.QuantityFoil;
            }
        }

        var publishedDeckIds = await _db.ForumPosts
            .AsNoTracking()
            .Where(p => p.AuthorId == userId)
            .Select(p => p.DeckId)
            .Distinct()
            .CountAsync(ct);

        return new PrivateStatsDto
        {
            CollectionValueUsd = Math.Round(total, 2),
            CopiesValued = copiesValued,
            UnpublishedDecks = Math.Max(stats.DecksBuilt - publishedDeckIds, 0),
        };
    }

    // ---- Stats helpers -----------------------------------------------------

    /// <summary>
    /// The cards appearing in the most of this user's decks.
    /// </summary>
    /// <remarks>
    /// Basic lands are dropped. Without that the answer is Forest, Island, Mountain,
    /// Plains, Swamp for every player alive, which says nothing about anybody. The query
    /// therefore over-fetches and filters after resolving, since "is a basic land" is a
    /// property of the card definition, not of the row.
    /// </remarks>
    private async Task<PlayedCardDto[]> GetMostPlayedCardsAsync(string userId, CancellationToken ct)
    {
        var counts = await _db.CollectionCards
            .AsNoTracking()
            .Where(cc => cc.Collection.UserId == userId && cc.Collection.IsDeck)
            .GroupBy(cc => cc.OracleId)
            .Select(g => new
            {
                OracleId = g.Key,
                DeckCount = g.Select(x => x.CollectionId).Distinct().Count(),
            })
            .OrderByDescending(x => x.DeckCount)
            .ThenBy(x => x.OracleId)
            .Take(RailSize * 4)
            .ToListAsync(ct);

        var results = new List<PlayedCardDto>(RailSize);

        foreach (var row in counts)
        {
            if (results.Count == RailSize)
                break;

            var card = await _cards.GetByOracleIdAsync(row.OracleId);
            if (card is null || IsBasicLand(card))
                continue;

            results.Add(new PlayedCardDto
            {
                OracleId = row.OracleId,
                Name = card.Name,
                ImageUriArtCrop = card.ImageUriArtCrop,
                ImageUriNormal = card.ImageUriNormal,
                DeckCount = row.DeckCount,
            });
        }

        return [.. results];
    }

    private static bool IsBasicLand(CardDefinition card) =>
        card.IsLand && card.Supertypes.Any(s => s.Equals("Basic", StringComparison.OrdinalIgnoreCase));

    private async Task<CommanderBriefDto[]> ResolveCommandersAsync(
        IEnumerable<(string OracleId, int Count)> counts, CancellationToken ct)
    {
        var results = new List<CommanderBriefDto>();

        foreach (var (oracleId, count) in counts)
        {
            ct.ThrowIfCancellationRequested();

            var card = await _cards.GetByOracleIdAsync(oracleId);
            if (card is null)
                continue; // A commander the card data no longer knows is left out, not rendered blank.

            results.Add(new CommanderBriefDto
            {
                OracleId = oracleId,
                Name = card.Name,
                ImageUriArtCrop = card.ImageUriArtCrop,
                ColorIdentity = ToColorLetters(card.ColorIdentity),
                DeckCount = count,
            });
        }

        return [.. results];
    }

    private async Task<CommanderBriefDto?> ResolveFavoriteCommanderAsync(
        string? oracleId, List<(string OracleId, int Count)> commanderCounts, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(oracleId))
            return null;

        var deckCount = commanderCounts.FirstOrDefault(c => c.OracleId == oracleId).Count;
        var resolved = await ResolveCommandersAsync([(oracleId, deckCount)], ct);
        return resolved.FirstOrDefault();
    }

    private async Task<ColorCountDto[]> BuildColorSpreadAsync(
        List<(string OracleId, int Count)> commanderCounts, CancellationToken ct)
    {
        var perColor = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (oracleId, count) in commanderCounts)
        {
            ct.ThrowIfCancellationRequested();

            var card = await _cards.GetByOracleIdAsync(oracleId);
            if (card is null)
                continue;

            foreach (var letter in ToColorLetters(card.ColorIdentity))
                perColor[letter] = perColor.GetValueOrDefault(letter) + count;
        }

        // WUBRG order, and only colours actually played â€” an empty bar for every colour a
        // user has never built reads as data when it is just an absence.
        return [.. ColorOrder
            .Where(perColor.ContainsKey)
            .Select(c => new ColorCountDto(c, perColor[c]))];
    }

    // ---- Shared plumbing ---------------------------------------------------

    private async Task<UserCommentDto[]> ToCommentDtosAsync(List<ForumComment> comments, CancellationToken ct)
    {
        if (comments.Count == 0)
            return [];

        var postIds = comments.Select(c => c.ForumPostId).Distinct().ToList();
        var posts = await _db.ForumPosts
            .AsNoTracking()
            .Where(p => postIds.Contains(p.Id))
            .Select(p => new { p.Id, p.DeckId })
            .ToDictionaryAsync(p => p.Id, p => p.DeckId, ct);

        var deckIds = posts.Values.Distinct().ToList();
        var deckNames = await _db.Collections
            .AsNoTracking()
            .Where(c => deckIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        return [.. comments.Select(c =>
        {
            posts.TryGetValue(c.ForumPostId, out var deckId);
            return new UserCommentDto
            {
                CommentId = c.Id,
                ForumPostId = c.ForumPostId,
                DeckId = deckId,
                DeckName = deckNames.GetValueOrDefault(deckId) ?? "Unknown Deck",
                Content = c.Content,
                CreatedAt = c.CreatedAt,
                Edited = WasEdited(c),
            };
        })];
    }

    /// <summary>Slack between the two insert-time timestamps; past it, a change is a real edit.</summary>
    private static readonly TimeSpan EditThreshold = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Whether a comment has been edited since it was posted.
    /// </summary>
    /// <remarks>
    /// Not <c>UpdatedAt &gt; CreatedAt</c>. <see cref="ForumComment"/> initialises the two
    /// from separate <c>DateTime.UtcNow</c> reads, so a brand-new comment's timestamps
    /// differ by however many ticks passed between them. That marked freshly posted
    /// comments as "edited" on the profile â€” not always, which is worse: whether a comment
    /// was libelled depended on where the clock happened to tick during construction.
    /// A genuine edit is a second request, and never within a second of the first.
    /// </remarks>
    private static bool WasEdited(ForumComment comment) =>
        comment.UpdatedAt - comment.CreatedAt > EditThreshold;

    /// <summary>
    /// Resolves a username to its user, case-insensitively.
    /// </summary>
    /// <remarks>
    /// Equality on a lower-cased column, not <c>LIKE</c>: a route value of "%" under LIKE
    /// is a wildcard that matches the first user in the table, which would hand a
    /// stranger's profile to anyone who typed one character.
    /// </remarks>
    private async Task<User> FindUserAsync(string username, CancellationToken ct)
    {
        var normalized = (username ?? string.Empty).ToLower();

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == normalized, ct);

        return user ?? throw new ResourceNotFoundException($"User '{username}' was not found.");
    }

    private async Task<User> FindUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        return user ?? throw new ResourceNotFoundException("Your account was not found.");
    }

    /// <summary>
    /// The avatar's public URL, or null when the user has none.
    /// </summary>
    /// <remarks>
    /// The <c>v</c> stamp is what lets the response itself be cached hard. Without it a
    /// replaced avatar keeps showing the old picture until the cache expires, which is
    /// exactly when a user is looking for the new one.
    /// </remarks>
    private static string? AvatarUrl(string username, DateTime? avatarUpdatedAt) =>
        avatarUpdatedAt is { } stamp
            ? $"/api/users/{Uri.EscapeDataString(username)}/avatar?v={stamp.Ticks}"
            : null;

    /// <summary>
    /// Rejects a pinned commander the card data does not know.
    /// </summary>
    /// <remarks>
    /// Saving it unchecked would "succeed" and then render nothing, because the profile
    /// projection skips oracle ids it cannot resolve â€” a setting that silently does not
    /// stick is worse than one that refuses.
    /// </remarks>
    private async Task<string?> ValidateCommanderAsync(string? oracleId, CancellationToken ct)
    {
        if (oracleId is null)
            return null;

        ct.ThrowIfCancellationRequested();

        var card = await _cards.GetByOracleIdAsync(oracleId);
        if (card is null)
            throw new InvalidRequestException("That commander could not be found.");

        return oracleId;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string[] ToColorLetters(IReadOnlyList<ManaColor> colors) =>
        [.. ColorOrder.Where(l => colors.Any(c => ColorToLetter(c) == l))];

    private static string ColorToLetter(ManaColor c) => c switch
    {
        ManaColor.White => "W",
        ManaColor.Blue => "U",
        ManaColor.Black => "B",
        ManaColor.Red => "R",
        ManaColor.Green => "G",
        _ => "C",
    };

    /// <summary>One owned collection or deck, flattened with its copy count.</summary>
    private sealed record OwnedCollection(
        Guid Id,
        string Name,
        string? CoverUri,
        string? Format,
        string? CommanderOracleId,
        bool IsDeck,
        DateTime UpdatedAt,
        int CardCount);
}
