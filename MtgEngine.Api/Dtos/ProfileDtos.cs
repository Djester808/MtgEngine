using System.ComponentModel.DataAnnotations;

namespace MtgEngine.Api.Dtos;

// ---- Public profile -------------------------------------------------------
//
// Everything in this half is served to anonymous callers. The rule the shapes
// here encode: counts and card choices are public, money is not. A profile says
// how many cards someone owns, never what they are worth — see MyProfileDto for
// the owner-only half.

/// <summary>A user's public profile: who they say they are, plus what the data says about them.</summary>
public sealed record UserProfileDto
{
    public string Username { get; init; } = string.Empty;

    /// <summary>Self-chosen display name, or null when they never set one — render the username then.</summary>
    public string? DisplayName { get; init; }

    public string? Tagline { get; init; }
    public string? Bio { get; init; }
    public string? FavoriteFormat { get; init; }

    /// <summary>
    /// Relative URL of the avatar image, or null when there is none (render initials).
    /// Carries a <c>v=</c> stamp so a replaced avatar is not served from cache.
    /// </summary>
    public string? AvatarUrl { get; init; }

    /// <summary>Account creation, not first post — a lurker has a join date too.</summary>
    public DateTime JoinedAt { get; init; }

    /// <summary>Published deck count. Kept alongside <see cref="Stats"/> because callers bind to it.</summary>
    public int DeckCount { get; init; }

    /// <summary>Comments this user has written.</summary>
    public int CommentCount { get; init; }

    public ProfileStatsDto Stats { get; init; } = new();

    /// <summary>The commander they pinned to their profile, when it still resolves.</summary>
    public CommanderBriefDto? FavoriteCommander { get; init; }

    /// <summary>Commanders they build with most, most-used first.</summary>
    public CommanderBriefDto[] TopCommanders { get; init; } = [];

    /// <summary>Cards appearing in the most of their decks — "what you actually play".</summary>
    public PlayedCardDto[] MostPlayedCards { get; init; } = [];

    /// <summary>Their published decks ranked by comments received.</summary>
    public ForumPostSummaryDto[] TopDecks { get; init; } = [];

    /// <summary>
    /// Decks touched most recently. On a public profile this is published decks only:
    /// an unpublished deck's name is not the user's contribution to the community, it is
    /// their private work in progress. The owner's own view includes everything.
    /// </summary>
    public ActiveDeckDto[] RecentlyActive { get; init; } = [];

    public ForumPostSummaryDto[] PublishedDecks { get; init; } = [];

    /// <summary>First page of comment history; the rest via <c>/api/users/{username}/comments</c>.</summary>
    public UserCommentDto[] RecentComments { get; init; } = [];
}

/// <summary>Counting stats. All public: none of them is a dollar figure.</summary>
public sealed record ProfileStatsDto
{
    public int DecksBuilt { get; init; }
    public int DecksPublished { get; init; }
    public int Collections { get; init; }

    /// <summary>Total copies owned, foils included, across every collection (decks excluded).</summary>
    public int CardsOwned { get; init; }

    /// <summary>Distinct cards owned, however many copies of each.</summary>
    public int DistinctCards { get; init; }

    public int CommentsPosted { get; init; }

    /// <summary>Comments other people left on their decks. Their own replies do not count.</summary>
    public int CommentsReceived { get; init; }

    /// <summary>Deck counts per colour of commander identity, in WUBRG order.</summary>
    public ColorCountDto[] ColorSpread { get; init; } = [];

    /// <summary>Deck counts per format, most-built first.</summary>
    public FormatCountDto[] Formats { get; init; } = [];

    /// <summary>Most recent deck or collection edit, or null for an account that has built nothing.</summary>
    public DateTime? LastActiveAt { get; init; }
}

public sealed record ColorCountDto(string Color, int DeckCount);

public sealed record FormatCountDto(string Format, int DeckCount);

/// <summary>Enough of a commander to render a chip with art.</summary>
public sealed record CommanderBriefDto
{
    public string OracleId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? ImageUriArtCrop { get; init; }
    public string[] ColorIdentity { get; init; } = [];

    /// <summary>How many of this user's decks it leads. Zero for a pinned favourite they have not built.</summary>
    public int DeckCount { get; init; }
}

/// <summary>A card and how many of the user's decks it turns up in.</summary>
public sealed record PlayedCardDto
{
    public string OracleId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? ImageUriArtCrop { get; init; }
    public string? ImageUriNormal { get; init; }

    /// <summary>Number of the user's decks containing it.</summary>
    public int DeckCount { get; init; }
}

/// <summary>A deck on the "recently active" rail.</summary>
public sealed record ActiveDeckDto
{
    public Guid DeckId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? CoverUri { get; init; }
    public string? Format { get; init; }
    public int CardCount { get; init; }
    public DateTime UpdatedAt { get; init; }

    /// <summary>Set when the deck is published, so the client can link to the forum post.</summary>
    public Guid? ForumPostId { get; init; }
}

/// <summary>One comment in a user's history, with enough context to link back to it.</summary>
public sealed record UserCommentDto
{
    public Guid CommentId { get; init; }
    public Guid ForumPostId { get; init; }
    public Guid DeckId { get; init; }
    public string DeckName { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }

    /// <summary>Whether the comment has been edited since it was posted.</summary>
    public bool Edited { get; init; }
}

/// <summary>A page of comment history.</summary>
public sealed record UserCommentPageDto
{
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public UserCommentDto[] Items { get; init; } = [];
}

/// <summary>A player as they appear in the community list.</summary>
public sealed record PlayerSummaryDto
{
    public string Username { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string? Tagline { get; init; }
    public string? AvatarUrl { get; init; }
    public DateTime JoinedAt { get; init; }
    public int DeckCount { get; init; }
    public int CommentCount { get; init; }
}

// ---- Owner-only -----------------------------------------------------------

/// <summary>
/// The signed-in user's own profile: the public projection, plus what only they may see.
/// </summary>
public sealed record MyProfileDto
{
    /// <summary>The same shape everyone else gets, so one component can render both.</summary>
    public UserProfileDto Profile { get; init; } = new();

    public string Email { get; init; } = string.Empty;

    public PrivateStatsDto PrivateStats { get; init; } = new();
}

/// <summary>
/// Stats withheld from the public profile.
/// </summary>
/// <remarks>
/// Collection value is the reason this record exists. Publishing what someone's cards are
/// worth advertises a target, and it is not the kind of thing a user expects a profile page
/// to disclose on their behalf.
/// </remarks>
public sealed record PrivateStatsDto
{
    /// <summary>Current market value of owned cards, in USD.</summary>
    public decimal CollectionValueUsd { get; init; }

    /// <summary>
    /// Copies that had a price to add up. Below <see cref="ProfileStatsDto.CardsOwned"/>
    /// whenever a printing has no listing, which keeps the total honest rather than
    /// implying every card was counted.
    /// </summary>
    public int CopiesValued { get; init; }

    /// <summary>Decks they have not published.</summary>
    public int UnpublishedDecks { get; init; }
}

/// <summary>Self-edit of the profile text fields. Every field is optional; sending null clears it.</summary>
/// <remarks>
/// A record with init properties rather than a positional one: the caps below have to sit
/// on the properties MVC binds, and on a positional record the attributes would need the
/// constructor-parameter form. Straight properties keep it unambiguous.
/// </remarks>
public sealed record UpdateProfileRequest
{
    [StringLength(64)] public string? DisplayName { get; init; }
    [StringLength(120)] public string? Tagline { get; init; }
    [StringLength(2000)] public string? Bio { get; init; }
    [StringLength(32)] public string? FavoriteFormat { get; init; }
    [StringLength(64)] public string? FavoriteCommanderOracleId { get; init; }
}

/// <summary>
/// A stored avatar, ready to be written to the response.
/// </summary>
/// <remarks>
/// Not JSON — this is how the service hands bytes to the controller without handing over
/// the tracked entity. <see cref="ContentType"/> is the type sniffed at upload, so the
/// response describes what is really in <see cref="Data"/>.
/// </remarks>
public sealed record AvatarContentDto(byte[] Data, string ContentType, string ETag, DateTime UpdatedAt);

/// <summary>What the client needs to enforce the avatar rules before it uploads.</summary>
public sealed record AvatarLimitsDto
{
    public int MaxBytes { get; init; }
    public int MaxDimension { get; init; }
    public string[] AcceptedContentTypes { get; init; } = [];
}
