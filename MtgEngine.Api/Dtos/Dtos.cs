using System.ComponentModel.DataAnnotations;

namespace MtgEngine.Api.Dtos;

// ---- Enums ------------------------------------------------

public enum ManaColorDto { C, W, U, B, R, G }
// Keep in sync with Domain.Enums.CardType — a missing member here silently strips the
// type from the DTO (Battles used to reach the client, and the AI judge, typeless).
public enum CardTypeDto { Creature, Instant, Sorcery, Enchantment, Artifact, Land, Planeswalker, Battle, Tribal, Token, Other }
// ---- Card / Permanent -------------------------------------

public sealed record CardDto
{
    public string CardId { get; init; } = string.Empty;
    public string OracleId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ManaCost { get; init; } = string.Empty;
    public int ManaValue { get; init; }
    public CardTypeDto[] CardTypes { get; init; } = [];
    public string[] Subtypes { get; init; } = [];
    public string[] Supertypes { get; init; } = [];
    public string OracleText { get; init; } = string.Empty;
    public int? Power { get; init; }
    public int? Toughness { get; init; }
    public int? StartingLoyalty { get; init; }
    public string[] Keywords { get; init; } = [];
    public string? ImageUriNormal { get; init; }
    public string? ImageUriLarge { get; init; }
    public string? ImageUriNormalBack { get; init; }
    public string? ImageUriSmall { get; init; }
    public string? ImageUriArtCrop { get; init; }
    public ManaColorDto[] ColorIdentity { get; init; } = [];
    public string OwnerId { get; init; } = string.Empty;
    public string? FlavorText { get; init; }
    public string? Artist { get; init; }
    public string? SetCode { get; init; }
    public string? Rarity { get; init; }
    public Dictionary<string, string> Legalities { get; init; } = [];
    public bool GameChanger { get; init; }
    // Null when Scryfall has no price data for this printing, so the client can
    // distinguish "unknown" from "listed at zero".
    public CardPricesDto? Prices { get; init; }
}

/// <summary>
/// Scryfall's daily market prices for one printing (TCGplayer USD, Cardmarket EUR,
/// Cardhoarder tix). Null fields mean no listing for that finish. The marketplace
/// ids let the client link straight to the listing (tcgplayer.com/product/{id},
/// cardhoarder.com/cards/{mtgoId}).
/// </summary>
public sealed record CardPricesDto
{
    public decimal? Usd { get; init; }
    public decimal? UsdFoil { get; init; }
    public decimal? UsdEtched { get; init; }
    public decimal? Eur { get; init; }
    public decimal? EurFoil { get; init; }
    public decimal? Tix { get; init; }
    public int? TcgplayerId { get; init; }
    public int? CardmarketId { get; init; }
    public int? MtgoId { get; init; }
}

// ---- Collection Management --------------------------------

public sealed record CollectionCardDto
{
    public Guid Id { get; init; }
    public string OracleId { get; init; } = string.Empty;
    public string? ScryfallId { get; init; }
    public int Quantity { get; init; }
    public int QuantityFoil { get; init; }
    public string? Notes { get; init; }
    public string Board { get; init; } = "main";
    public DateTime AddedAt { get; init; }
    // Market price when the row was created; null for rows predating price tracking.
    public decimal? PriceUsdAtAdd { get; init; }
    public decimal? PriceUsdFoilAtAdd { get; init; }
    public CardDto? CardDetails { get; init; }
}

/// <summary>One day's prices for one printing, for the price-history chart.</summary>
public sealed record PricePointDto(
    DateTime Date,
    decimal? Usd,
    decimal? UsdFoil,
    decimal? Eur,
    decimal? Tix);

public sealed record CollectionDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? CoverUri { get; init; }
    public int CardCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record CollectionDetailDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? CoverUri { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public CollectionCardDto[] Cards { get; init; } = [];
}

// Length caps live on the request DTOs: SQLite ignores the EF model's HasMaxLength, but
// MVC validates these annotations before the service runs — a 400 ProblemDetails, not an
// unbounded blob written to disk.
public sealed record CreateCollectionRequest(
    [StringLength(200, MinimumLength = 1)] string Name,
    [StringLength(4000)] string? Description = null);
public sealed record UpdateCollectionRequest(
    [StringLength(200, MinimumLength = 1)] string Name,
    [StringLength(4000)] string? Description = null,
    [StringLength(2048)] string? CoverUri = null);
// Validation attributes on positional records must target the constructor
// parameter (no `property:` prefix) — MVC throws at bind time otherwise.
public sealed record AddCardToCollectionRequest(
    string OracleId,
    string? ScryfallId = null,
    // Same bound as UpdateCollectionCardRequest — a card must be addable at any quantity
    // it can later be updated to (collections legitimately hold thousands of a common).
    [System.ComponentModel.DataAnnotations.Range(0, 9999)] int Quantity = 1,
    [System.ComponentModel.DataAnnotations.Range(0, 9999)] int QuantityFoil = 0,
    string? Notes = null,
    string Board = "main"
);
public sealed record UpdateCollectionCardRequest(
    [System.ComponentModel.DataAnnotations.Range(0, 9999)] int Quantity,
    [System.ComponentModel.DataAnnotations.Range(0, 9999)] int QuantityFoil,
    string? ScryfallId = null,
    string? Notes = null
);

/// <summary>
/// Moves copies of one card row to another collection. Null quantities move the whole
/// row; the caps match <see cref="UpdateCollectionCardRequest"/> so anything holdable is
/// movable.
/// </summary>
public sealed record MoveCardRequest(
    [Required] Guid TargetCollectionId,
    [System.ComponentModel.DataAnnotations.Range(0, 9999)] int? Quantity = null,
    [System.ComponentModel.DataAnnotations.Range(0, 9999)] int? QuantityFoil = null
);

/// <summary>Result of a move: the row as it now stands in each collection.</summary>
public sealed record MoveCardResultDto
{
    /// <summary>The destination row after folding in the moved copies.</summary>
    public CollectionCardDto Target { get; init; } = null!;
    /// <summary>What is left in the source, or null when the row moved whole and was removed.</summary>
    public CollectionCardDto? SourceRemainder { get; init; }
}

/// <summary>Moves several whole card rows to another collection in one request.</summary>
public sealed record MoveCardsRequest(
    [Required] Guid TargetCollectionId,
    [Required][MaxLength(500)] Guid[] CardIds
);

public sealed record MoveCardsResultDto
{
    public int CardsMoved { get; init; }
    public int CardsFolded { get; init; }
    public int CopiesTransferred { get; init; }
    /// <summary>Ids of the source rows that no longer exist, so the client can drop them.</summary>
    public Guid[] RemovedCardIds { get; init; } = [];
}

/// <summary>Folds every card of one collection into another.</summary>
public sealed record MergeCollectionsRequest(
    [Required] Guid SourceCollectionId,
    /// <summary>Delete the emptied source collection afterwards. Off by default — merging is destructive enough.</summary>
    bool DeleteSource = false
);

public sealed record MergeCollectionsResultDto
{
    /// <summary>Rows that had no counterpart and moved across whole.</summary>
    public int CardsMoved { get; init; }
    /// <summary>Rows that matched an existing printing and had their quantities added to it.</summary>
    public int CardsFolded { get; init; }
    /// <summary>Total copies (normal + foil) transferred.</summary>
    public int CopiesTransferred { get; init; }
    public bool SourceDeleted { get; init; }
    public CollectionDetailDto Target { get; init; } = null!;
}

// ---- Deck Management (reuses CollectionCardDto for cards) --

public sealed record DeckDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? CoverUri { get; init; }
    public string? Format { get; init; }
    public string? CommanderOracleId { get; init; }
    public int CardCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record DeckDetailDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? CoverUri { get; init; }
    public string? Format { get; init; }
    public string? CommanderOracleId { get; init; }
    public string[] Tags { get; init; } = [];
    public string? Notes { get; init; }
    public bool IsPublished { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public CollectionCardDto[] Cards { get; init; } = [];
}

public sealed record CreateDeckRequest(
    [StringLength(200, MinimumLength = 1)] string Name,
    [StringLength(2048)] string? CoverUri = null,
    [StringLength(40)] string? Format = null,
    [StringLength(64)] string? CommanderOracleId = null);
public sealed record UpdateDeckRequest(
    [StringLength(200, MinimumLength = 1)] string Name,
    [StringLength(2048)] string? CoverUri = null,
    [StringLength(40)] string? Format = null,
    [StringLength(64)] string? CommanderOracleId = null,
    [MaxLength(50)] string[]? Tags = null,
    [StringLength(8000)] string? Notes = null);

public sealed record ImportDeckRequest(
    [StringLength(200)] string Name,
    [StringLength(500_000)] string? Text = null,
    [StringLength(2048)] string? Url = null,
    [StringLength(40)] string? Format = null
);

public sealed record ImportDeckResult(
    DeckDetailDto Deck,
    int CardsResolved,
    int CardsTotal,
    IReadOnlyList<string> UnresolvedCards
);

/// <summary>
/// A browsable candidate. Carries a printing id so the card can be added to a deck
/// without a second round trip per row.
/// </summary>
/// <param name="Score">
/// Synergy score, present only when the pool was ranked by it. Returned with the row so the
/// list renders the number it was sorted by, rather than fetching scores separately and
/// risking a different answer than the one that decided the order.
/// </param>
public sealed record CandidateCardDto(
    CardDto Card, string? ScryfallId, string? SetCode, string? SetName, int? Score = null);

/// <summary>Score several cards against one commander in a single call.</summary>
/// <param name="Focus">
/// Themes the player has asked to build around, e.g. "wolf". Scoring without them judges
/// against the commander's printed text alone, so a card satisfying a numeric requirement
/// can outrank the tribe the player actually asked for.
/// </param>
/// <param name="Mode">"ideal" (default) or "deck-aware". See doctrine §10.2.</param>
public sealed record SynergyBatchRequest(
    string CommanderOracleId,
    string[] CardOracleIds,
    string[]? Focus = null,
    string? Mode = null);

/// <summary>A page of the browsable candidate pool, with the unpaged total.</summary>
public sealed record CandidatePoolDto(int Total, CandidateCardDto[] Cards);

/// <param name="ReleasedAt">Null for the handful of sets with no dated printing.</param>
public sealed record SetSummaryDto(string Code, string Name, int CardCount, DateOnly? ReleasedAt = null);

/// <summary>A recently released set, with enough detail to name it in the UI.</summary>
public sealed record RecentSetDto(string Code, string Name, DateOnly ReleasedAt, int LegalCardCount);

// ---- Auth -----------------------------------------------------

public sealed record RegisterRequest(
    [StringLength(64, MinimumLength = 1)] string Username,
    [StringLength(256, MinimumLength = 3)] string Email,
    [StringLength(200, MinimumLength = 8)] string Password);
public sealed record LoginRequest(string Username, string Password);
public sealed record AuthTokenResponse(string Token, string Username);

public sealed record RulingDto(string Source, string PublishedAt, string Comment);

public sealed record PrintingDto
{
    public string ScryfallId { get; init; } = string.Empty;
    public string SetCode { get; init; } = string.Empty;
    public string SetName { get; init; } = string.Empty;
    public string? CollectorNumber { get; init; }
    public string? ImageUriSmall { get; init; }
    public string? ImageUriNormal { get; init; }
    public string? ImageUriLarge { get; init; }
    public string? ImageUriNormalBack { get; init; }
    // Per-printing text (can differ per set due to errata, new art, etc.)
    public string? OracleText { get; init; }
    public string? FlavorText { get; init; }
    public string? Artist { get; init; }
    public string? ManaCost { get; init; }
    // Per-printing prices; null when Scryfall lists none for this printing.
    public CardPricesDto? Prices { get; init; }
};

// ---- Deck suggestions ---------------------------------------

public sealed record DeckSuggestionsRequest
{
    public string CommanderOracleId { get; init; } = string.Empty;
    public string CommanderName { get; init; } = string.Empty;
    public string CommanderText { get; init; } = string.Empty;
    public string[] DeckCardNames { get; init; } = [];
    public string[] DeckTags { get; init; } = [];
    public string[] SuggestionTags { get; init; } = [];
}

public sealed record SuggestedCardDto
{
    public string Name { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public int Score { get; init; }
    public string? ScryfallId { get; init; }
    public CardDto? Card { get; init; }
}

public sealed record DeckSuggestionsDto
{
    public SuggestedCardDto[] LatestSet { get; init; } = [];
    public SuggestedCardDto[] TopSynergy { get; init; } = [];
    public SuggestedCardDto[] GameChangers { get; init; } = [];

    /// <summary>
    /// The sets <see cref="LatestSet"/> was drawn from, newest first. Shown in the UI so
    /// "latest sets" is a claim the user can check rather than take on trust.
    /// </summary>
    public RecentSetDto[] LatestSetSources { get; init; } = [];

    /// <summary>What the validation layer discarded from the model's proposals.</summary>
    public SuggestionDiagnosticsDto Diagnostics { get; init; } = new();
}

/// <summary>
/// Why suggestions were dropped between the model proposing them and the response.
/// </summary>
/// <remarks>
/// Validation turns hallucinations into missing results rather than wrong ones, which
/// is correct but invisible: a category returning nothing looks the same as a category
/// the model declined to fill. Reporting the rejections makes a 100%-rejection bug
/// observable instead of silent.
/// </remarks>
public sealed record SuggestionDiagnosticsDto
{
    /// <summary>Cards the model proposed, before validation.</summary>
    public int Proposed { get; init; }

    /// <summary>Cards returned to the caller, after validation and de-duplication.</summary>
    public int Accepted { get; init; }

    /// <summary>Rejection reason -> count. Empty when nothing was discarded.</summary>
    public Dictionary<string, int> Rejected { get; init; } = [];
}

// ---- Mana fine-tune -----------------------------------------

public sealed record ManaFineTuneRequest
{
    public string Format { get; init; } = string.Empty;
    public string[] DeckCardNames { get; init; } = [];
    public int CurrentLands { get; init; }
    public int RecommendedLands { get; init; }
    public double AvgCmc { get; init; }
    public string[] ActiveColors { get; init; } = [];
}

public sealed record ManaLandSuggestion
{
    public string Name { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;

    /// <summary>Newest printing, so the client can add the card directly.</summary>
    public string? ScryfallId { get; init; }

    /// <summary>Resolved card data; suggestions that fail to resolve are dropped.</summary>
    public CardDto? Card { get; init; }
}

public sealed record ManaFineTuneDto
{
    public string[] Advice { get; init; } = [];
    public ManaLandSuggestion[] LandSuggestions { get; init; } = [];
}

// ---- Synergy scoring ----------------------------------------

public sealed record SynergyRequest
{
    public string CommanderOracleId { get; init; } = string.Empty;
    public string CommanderName { get; init; } = string.Empty;
    public string CommanderText { get; init; } = string.Empty;
    public string CardOracleId { get; init; } = string.Empty;
    public string CardName { get; init; } = string.Empty;
    public string CardText { get; init; } = string.Empty;
    public string[] DeckCardNames { get; init; } = [];
}

public sealed record SynergyResultDto
{
    public int Score { get; init; }
    public string Reason { get; init; } = string.Empty;
}

/// <summary>One card's synergy verdict within a scored deck.</summary>
public sealed record ScoredCardDto
{
    public string OracleId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Score { get; init; }
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Synergy scores for every card in a deck, from a single batched call.
/// </summary>
/// <remarks>
/// Scoring cards one at a time costs one request each; a 99-card deck is one request
/// here. Results are written to the same cache the per-card endpoint reads, so scoring
/// a deck warms it for subsequent single-card lookups.
/// </remarks>
public sealed record DeckScoreDto
{
    public ScoredCardDto[] Cards { get; init; } = [];

    /// <summary>Mean score across scored cards, as a rough deck-cohesion signal.</summary>
    public int AverageScore { get; init; }

    /// <summary>Cards scoring below <see cref="WeakThreshold"/> — the obvious swap candidates.</summary>
    public ScoredCardDto[] WeakestCards { get; init; } = [];

    public const int WeakThreshold = 55;
}

// ---- AI deck build ------------------------------------------

public sealed record AiBuildRequest
{
    public string CommanderOracleId { get; init; } = string.Empty;

    /// <summary>1–5. Validated: an out-of-range value would disable the game-changer gate.</summary>
    [System.ComponentModel.DataAnnotations.Range(1, 5)]
    public int Bracket { get; init; } = 3;

    public string PriceRange { get; init; } = "any";       // "budget" | "mid" | "any"
    public bool IncludeSideboard { get; init; } = false;
    public bool IncludeMaybeboard { get; init; } = false;
}

public sealed record AiRefineRequest
{
    /// <summary>Bracket the refined deck must still respect. Defaults to Commander's mid bracket.</summary>
    [System.ComponentModel.DataAnnotations.Range(1, 5)]
    public int Bracket { get; init; } = 3;

    public string PriceRange { get; init; } = "any";

    /// <summary>Upper bound on swaps, so a refine cannot quietly rebuild the whole deck.</summary>
    [System.ComponentModel.DataAnnotations.Range(0, 30)]
    public int MaxSwaps { get; init; } = 10;
}

/// <summary>One card exchanged for another during a refine.</summary>
public sealed record CardSwapDto
{
    public string Out { get; init; } = string.Empty;
    public string In { get; init; } = string.Empty;
    public string Why { get; init; } = string.Empty;
}

public sealed record AiRefineResultDto
{
    public CardSwapDto[] Swaps { get; init; } = [];

    /// <summary>Swaps the model proposed but that could not be applied, with the reason.</summary>
    public Dictionary<string, int> RejectedByReason { get; init; } = [];

    public int DeckSizeBefore { get; init; }
    public int DeckSizeAfter { get; init; }
}

public sealed record AiBuildResultDto
{
    public int CardsAdded { get; init; }
    public int SideboardAdded { get; init; }
    public int MaybeboardAdded { get; init; }
    public int CardsSkipped { get; init; }

    /// <summary>Main-deck slots this build was asked to fill.</summary>
    public int MainTarget { get; init; }

    /// <summary>
    /// Main-deck slots still empty after substitutes were exhausted. Zero for a
    /// complete deck; non-zero means the result is not legal and needs manual filling.
    /// </summary>
    public int MainShortfall { get; init; }

    /// <summary>Rejection reason -> count, for the cards that were discarded.</summary>
    public Dictionary<string, int> SkippedByReason { get; init; } = [];
}

// ---- Forum --------------------------------------------------

public sealed record ForumPostSummaryDto
{
    public Guid Id { get; init; }
    public Guid DeckId { get; init; }
    public string AuthorUsername { get; init; } = string.Empty;
    public string DeckName { get; init; } = string.Empty;
    public string? DeckCoverUri { get; init; }
    public string? DeckFormat { get; init; }
    public string? Description { get; init; }
    public string[] ColorIdentity { get; init; } = [];
    public int CardCount { get; init; }
    public int CommentCount { get; init; }
    public DateTime PublishedAt { get; init; }
}

public sealed record ForumPostDetailDto
{
    public Guid Id { get; init; }
    public Guid DeckId { get; init; }
    public string AuthorId { get; init; } = string.Empty;
    public string AuthorUsername { get; init; } = string.Empty;
    public string DeckName { get; init; } = string.Empty;
    public string? DeckCoverUri { get; init; }
    public string? DeckFormat { get; init; }
    public string? CommanderOracleId { get; init; }
    public string? CommanderImageUri { get; init; }
    public string? CommanderName { get; init; }
    public string? Description { get; init; }
    public string[] ColorIdentity { get; init; } = [];
    public DateTime PublishedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public CollectionCardDto[] Cards { get; init; } = [];
    public ForumCommentDto[] Comments { get; init; } = [];
}

public sealed record ForumCommentDto
{
    public Guid Id { get; init; }
    public string AuthorId { get; init; } = string.Empty;
    public string AuthorUsername { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record UserProfileDto
{
    public string Username { get; init; } = string.Empty;
    public DateTime JoinedAt { get; init; }
    public int DeckCount { get; init; }
    public int CommentCount { get; init; }
    public ForumPostSummaryDto[] PublishedDecks { get; init; } = [];
    public UserCommentDto[] RecentComments { get; init; } = [];
}

public sealed record UserCommentDto
{
    public Guid CommentId { get; init; }
    public Guid ForumPostId { get; init; }
    public string DeckName { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}

public sealed record UserPreferencesDto
{
    [StringLength(200)] public string? DeckLayout { get; init; }
    [StringLength(200)] public string? ForumLayout { get; init; }
    [StringLength(200)] public string? ForumSort { get; init; }
}

public sealed record PublishDeckRequest(Guid DeckId, string? Description = null);
public sealed record CreateCommentRequest(string Content);
public sealed record UpdateCommentRequest(string Content);

// ---- Commanders --------------------------------------------------

public sealed record CommanderSummaryDto
{
    public string OracleId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? ImageUri { get; init; }
    public string? ImageUriArtCrop { get; init; }
    public string[] ColorIdentity { get; init; } = [];
    public string? ManaCost { get; init; }
    public int DeckCount { get; init; }
    public int Rank { get; init; }
}

public sealed record CommanderProfileDto
{
    public string OracleId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? ImageUri { get; init; }
    public string? ImageUriArtCrop { get; init; }
    public string[] ColorIdentity { get; init; } = [];
    public string? ManaCost { get; init; }
    public string? OracleText { get; init; }
    public int DeckCount { get; init; }
    public int Rank { get; init; }
    public TagCountDto[] TopTags { get; init; } = [];
}

public sealed record TagCountDto(string Tag, int Count);

public sealed record CommanderCardEntryDto
{
    public CardDto Card { get; init; } = null!;
    public int DeckCount { get; init; }
    public int TotalDecks { get; init; }
    public double InclusionPercent { get; init; }
    public bool IsGameChanger { get; init; }
}

public sealed record CommanderCardsDto
{
    public int TotalDecks { get; init; }
    public CommanderCardEntryDto[] Cards { get; init; } = [];
}

public sealed record CommanderHistoryPointDto(string Month, int DeckCount);

public sealed record SimilarCommanderDto
{
    public string OracleId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? ImageUri { get; init; }
    public string? ImageUriArtCrop { get; init; }
    public string[] ColorIdentity { get; init; } = [];
    public int DeckCount { get; init; }
    public int SharedCards { get; init; }
    public int Rank { get; init; }
}

public sealed record CommanderDeckDto
{
    public Guid ForumPostId { get; init; }
    public Guid DeckId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string AuthorUsername { get; init; } = string.Empty;
    public DateTime PublishedAt { get; init; }
    public int CardCount { get; init; }
    public int Bracket { get; init; }
    public string[] Tags { get; init; } = [];
    public string[] ColorIdentity { get; init; } = [];
}
