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
    public string? ImageUriNormal { get; init; }
    public string? ImageUriSmall  { get; init; }
    public ManaColorDto[] ColorIdentity { get; init; } = [];
    public string OwnerId         { get; init; } = string.Empty;
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
