namespace MtgEngine.Api.Dtos;

// ---- Enums ------------------------------------------------

public enum ManaColorDto    { C, W, U, B, R, G }
public enum CardTypeDto     { Creature, Instant, Sorcery, Enchantment, Artifact, Land, Planeswalker }
public enum PhaseDto        { Beginning, PreCombatMain, Combat, PostCombatMain, Ending }
public enum StepDto         { Untap, Upkeep, Draw, Main, BeginningOfCombat, DeclareAttackers, DeclareBlockers, FirstStrikeDamage, CombatDamage, EndOfCombat, End, Cleanup }
public enum GameResultDto   { InProgress, Player1Wins, Player2Wins, Draw }
public enum StackObjectTypeDto { Spell, ActivatedAbility, TriggeredAbility }
public enum CounterTypeDto  { PlusOnePlusOne, MinusOneMinusOne, Loyalty, Charge, Poison }

// ---- Card / Permanent -------------------------------------

public sealed record CardDto
{
    public string CardId          { get; init; } = string.Empty;
    public string OracleId        { get; init; } = string.Empty;
    public string Name            { get; init; } = string.Empty;
    public string ManaCost        { get; init; } = string.Empty;
    public int    ManaValue       { get; init; }
    public CardTypeDto[] CardTypes{ get; init; } = [];
    public string[] Subtypes      { get; init; } = [];
    public string[] Supertypes    { get; init; } = [];
    public string OracleText      { get; init; } = string.Empty;
    public int?   Power           { get; init; }
    public int?   Toughness       { get; init; }
    public int?   StartingLoyalty { get; init; }
    public string[] Keywords      { get; init; } = [];
    public string? ImageUriNormal     { get; init; }
    public string? ImageUriNormalBack { get; init; }
    public string? ImageUriSmall      { get; init; }
    public string? ImageUriArtCrop    { get; init; }
    public ManaColorDto[] ColorIdentity { get; init; } = [];
    public string OwnerId         { get; init; } = string.Empty;
    public string? FlavorText     { get; init; }
    public string? Artist         { get; init; }
    public string? SetCode        { get; init; }
    public string? Rarity         { get; init; }
    public Dictionary<string, string> Legalities { get; init; } = [];
    public bool GameChanger { get; init; }
}

public sealed record PermanentDto
{
    public string PermanentId      { get; init; } = string.Empty;
    public CardDto SourceCard      { get; init; } = null!;
    public string ControllerId     { get; init; } = string.Empty;
    public bool   IsTapped         { get; init; }
    public bool   HasSummoningSickness { get; init; }
    public int    DamageMarked     { get; init; }
    public Dictionary<string, int> Counters { get; init; } = [];
    public string[] Attachments    { get; init; } = [];
    public int?   EffectivePower   { get; init; }
    public int?   EffectiveToughness { get; init; }
}

// ---- Stack ------------------------------------------------

public sealed record StackObjectDto
{
    public string StackObjectId    { get; init; } = string.Empty;
    public StackObjectTypeDto Type { get; init; }
    public string ControllerId     { get; init; } = string.Empty;
    public string Description      { get; init; } = string.Empty;
    public string SourceCardName   { get; init; } = string.Empty;
    public TargetDto[] Targets     { get; init; } = [];
}

public sealed record TargetDto(string Type, string Id);

// ---- Players / Game state ---------------------------------

public sealed record ManaPoolDto
{
    public Dictionary<string, int> Amounts { get; init; } = [];
    public int Total { get; init; }
}

public sealed record PlayerStateDto
{
    public string PlayerId      { get; init; } = string.Empty;
    public string Name          { get; init; } = string.Empty;
    public int    Life          { get; init; }
    public int    PoisonCounters{ get; init; }
    public ManaPoolDto ManaPool { get; init; } = null!;
    public int    HandCount     { get; init; }
    public int    LibraryCount  { get; init; }
    public int    GraveyardCount{ get; init; }
    public int    ExileCount    { get; init; }

    // Only populated for the local player viewing their own state
    public CardDto[] Hand       { get; init; } = [];
    public CardDto[] Graveyard  { get; init; } = [];
    public CardDto[] Exile      { get; init; } = [];
    public bool HasLandPlayedThisTurn { get; init; }
}

public sealed record CombatStateDto
{
    public string[] Attackers   { get; init; } = [];
    public Dictionary<string, string[]> AttackersToBlockers { get; init; } = [];
    public bool AttackersDeclared { get; init; }
    public bool BlockersDeclared  { get; init; }
}

public sealed record GameStateDto
{
    public string GameId           { get; init; } = string.Empty;
    public PlayerStateDto[] Players{ get; init; } = [];
    public PermanentDto[] Battlefield { get; init; } = [];
    public StackObjectDto[] Stack  { get; init; } = [];
    public int    Turn             { get; init; }
    public string ActivePlayerId   { get; init; } = string.Empty;
    public string PriorityPlayerId { get; init; } = string.Empty;
    public PhaseDto CurrentPhase   { get; init; }
    public StepDto  CurrentStep    { get; init; }
    public GameResultDto Result    { get; init; }
    public CombatStateDto? Combat  { get; init; }
}

// ---- SignalR diff ------------------------------------------

public sealed record GameStateDiffDto
{
    public PermanentDto[] ChangedPermanents  { get; init; } = [];
    public string[]  RemovedPermanentIds     { get; init; } = [];
    public StackObjectDto[] Stack            { get; init; } = [];
    public string    PriorityPlayerId        { get; init; } = string.Empty;
    public PhaseDto  CurrentPhase            { get; init; }
    public StepDto   CurrentStep             { get; init; }
    public GameResultDto Result              { get; init; }
    public CombatStateDto? Combat            { get; init; }
    public PlayerStateDto[] PlayerUpdates    { get; init; } = [];
}

// ---- Request / Response shapes ----------------------------

public sealed record CreateGameRequest(
    string Player1Name,
    string Player2Name,
    string[] Player1DeckList,
    string[] Player2DeckList
);

public sealed record CreateGameResponse(
    string GameId,
    string Player1Token,
    string Player2Token
);

public sealed record JoinGameRequest(string PlayerToken);

public sealed record JoinGameResponse(
    string GameId,
    string PlayerToken,
    string PlayerId,
    GameStateDto InitialState
);

public sealed record CastSpellRequest(string CardId, string[] TargetIds);
public sealed record DeclareBlockersRequest(Dictionary<string, string> BlockerToAttacker);
public sealed record SetBlockerOrderRequest(string AttackerId, string[] OrderedBlockerIds);

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
    public CardDto? CardDetails { get; init; }
}

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

public sealed record CreateCollectionRequest(string Name, string? Description = null);
public sealed record UpdateCollectionRequest(string Name, string? Description = null, string? CoverUri = null);
public sealed record AddCardToCollectionRequest(
    string OracleId,
    string? ScryfallId = null,
    int Quantity = 1,
    int QuantityFoil = 0,
    string? Notes = null,
    string Board = "main"
);
public sealed record UpdateCollectionCardRequest(
    int Quantity,
    int QuantityFoil,
    string? ScryfallId = null,
    string? Notes = null
);

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
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public CollectionCardDto[] Cards { get; init; } = [];
}

public sealed record CreateDeckRequest(string Name, string? CoverUri = null, string? Format = null, string? CommanderOracleId = null);
public sealed record UpdateDeckRequest(string Name, string? CoverUri = null, string? Format = null, string? CommanderOracleId = null, string[]? Tags = null, string? Notes = null);

public sealed record ImportDeckRequest(
    string Name,
    string? Text   = null,
    string? Url    = null,
    string? Format = null
);

public sealed record ImportDeckResult(
    DeckDetailDto Deck,
    int CardsResolved,
    int CardsTotal,
    IReadOnlyList<string> UnresolvedCards
);

public sealed record SetSummaryDto(string Code, string Name, int CardCount);

// ---- Auth -----------------------------------------------------

public sealed record RegisterRequest(string Username, string Email, string Password);
public sealed record LoginRequest(string Username, string Password);
public sealed record AuthTokenResponse(string Token, string Username);

public sealed record RulingDto(string Source, string PublishedAt, string Comment);

public sealed record PrintingDto
{
    public string  ScryfallId      { get; init; } = string.Empty;
    public string  SetCode         { get; init; } = string.Empty;
    public string  SetName         { get; init; } = string.Empty;
    public string? CollectorNumber { get; init; }
    public string? ImageUriSmall      { get; init; }
    public string? ImageUriNormal     { get; init; }
    public string? ImageUriNormalBack { get; init; }
    // Per-printing text (can differ per set due to errata, new art, etc.)
    public string? OracleText         { get; init; }
    public string? FlavorText      { get; init; }
    public string? Artist          { get; init; }
    public string? ManaCost        { get; init; }
};

// ---- Deck suggestions ---------------------------------------

public sealed record DeckSuggestionsRequest
{
    public string CommanderOracleId { get; init; } = string.Empty;
    public string CommanderName     { get; init; } = string.Empty;
    public string CommanderText     { get; init; } = string.Empty;
    public string[] DeckCardNames   { get; init; } = [];
    public string[] DeckTags        { get; init; } = [];
    public string[] SuggestionTags  { get; init; } = [];
}

public sealed record SuggestedCardDto
{
    public string   Name       { get; init; } = string.Empty;
    public string   Reason     { get; init; } = string.Empty;
    public int      Score      { get; init; }
    public string?  ScryfallId { get; init; }
    public CardDto? Card       { get; init; }
}

public sealed record DeckSuggestionsDto
{
    public SuggestedCardDto[] LatestSet       { get; init; } = [];
    public SuggestedCardDto[] TopSynergy      { get; init; } = [];
    public SuggestedCardDto[] GameChangers    { get; init; } = [];
    public SuggestedCardDto[] NotableMentions { get; init; } = [];
}

// ---- Mana fine-tune -----------------------------------------

public sealed record ManaFineTuneRequest
{
    public string   Format           { get; init; } = string.Empty;
    public string[] DeckCardNames    { get; init; } = [];
    public int      CurrentLands     { get; init; }
    public int      RecommendedLands { get; init; }
    public double   AvgCmc           { get; init; }
    public string[] ActiveColors     { get; init; } = [];
}

public sealed record ManaLandSuggestion
{
    public string Name   { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed record ManaFineTuneDto
{
    public string[]              Advice          { get; init; } = [];
    public ManaLandSuggestion[]  LandSuggestions { get; init; } = [];
}

// ---- Synergy scoring ----------------------------------------

public sealed record SynergyRequest
{
    public string CommanderOracleId { get; init; } = string.Empty;
    public string CommanderName     { get; init; } = string.Empty;
    public string CommanderText     { get; init; } = string.Empty;
    public string CardOracleId      { get; init; } = string.Empty;
    public string CardName          { get; init; } = string.Empty;
    public string CardText          { get; init; } = string.Empty;
    public string[] DeckCardNames   { get; init; } = [];
}

public sealed record SynergyResultDto
{
    public int    Score  { get; init; }
    public string Reason { get; init; } = string.Empty;
}

// ---- AI deck build ------------------------------------------

public sealed record AiBuildRequest
{
    public string CommanderOracleId  { get; init; } = string.Empty;
    public int    Bracket            { get; init; } = 3;           // 1–5
    public string PriceRange         { get; init; } = "any";       // "budget" | "mid" | "any"
    public bool   IncludeSideboard   { get; init; } = false;
    public bool   IncludeMaybeboard  { get; init; } = false;
}

public sealed record AiBuildResultDto
{
    public int CardsAdded        { get; init; }
    public int SideboardAdded    { get; init; }
    public int MaybeboardAdded   { get; init; }
    public int CardsSkipped      { get; init; }
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

public sealed record PublishDeckRequest(Guid DeckId, string? Description = null);
public sealed record CreateCommentRequest(string Content);
public sealed record UpdateCommentRequest(string Content);
