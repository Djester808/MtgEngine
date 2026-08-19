using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Overridable no-op implementations of the wide service interfaces, so a test can supply
/// only the one or two methods it exercises.
/// </summary>
/// <remarks>
/// Every member throws by default rather than returning empty. A test that reaches an
/// unstubbed call has wandered outside what it meant to cover, and should say so loudly
/// instead of quietly asserting against a blank result.
/// </remarks>
public abstract class StubScryfallService : IScryfallService
{
    private static T NotStubbed<T>(string member) =>
        throw new NotSupportedException($"{member} was called but this test did not stub it.");

    public virtual Task<CardDefinition?> GetByOracleIdAsync(string oracleId) =>
        NotStubbed<Task<CardDefinition?>>(nameof(GetByOracleIdAsync));

    public virtual Task<CardDefinition?> GetByNameAsync(string name) =>
        NotStubbed<Task<CardDefinition?>>(nameof(GetByNameAsync));

    public virtual Task<CardDefinition?> GetByScryfallIdAsync(string scryfallId) =>
        NotStubbed<Task<CardDefinition?>>(nameof(GetByScryfallIdAsync));

    public virtual Task<PrintingDto[]> GetPrintingsAsync(string oracleId) =>
        NotStubbed<Task<PrintingDto[]>>(nameof(GetPrintingsAsync));

    public virtual Task<RulingDto[]> GetRulingsAsync(string oracleId) =>
        NotStubbed<Task<RulingDto[]>>(nameof(GetRulingsAsync));

    public virtual Task<SetSummaryDto[]> GetSetsAsync(string? filterQuery = null) =>
        NotStubbed<Task<SetSummaryDto[]>>(nameof(GetSetsAsync));

    public virtual Task<CardDefinition[]> SearchAsync(
        string query, int limit = 20, int offset = 0, string sortBy = "name", string sortDir = "asc",
        bool matchCase = false, bool matchWord = false, bool useRegex = false) =>
        NotStubbed<Task<CardDefinition[]>>(nameof(SearchAsync));

    public virtual Task<IReadOnlySet<string>> GetRecentSetCodesAsync(int monthsBack = 6, int? maxSets = null) =>
        NotStubbed<Task<IReadOnlySet<string>>>(nameof(GetRecentSetCodesAsync));

    public virtual Task<IReadOnlyList<RecentSetDto>> GetRecentSetsAsync(int monthsBack = 6, int? maxSets = null) =>
        NotStubbed<Task<IReadOnlyList<RecentSetDto>>>(nameof(GetRecentSetsAsync));

    public virtual Task<string[]> GetRecentCardNamesAsync(
        IReadOnlySet<string> setCodes, IReadOnlySet<ManaColor> commanderColors,
        IReadOnlySet<string>? allowedRarities = null, bool debutOnly = false) =>
        NotStubbed<Task<string[]>>(nameof(GetRecentCardNamesAsync));

    public virtual Task<(CardDefinition[] Cards, int Total)> GetCandidatePoolAsync(
        IReadOnlySet<ManaColor> commanderColors, CardDefinition? commander = null, string? query = null,
        IReadOnlySet<string>? setCodes = null, bool gameChangersOnly = false,
        CardType types = CardType.None, int? cmcMin = null, int? cmcMax = null,
        int limit = 50, int offset = 0) =>
        NotStubbed<Task<(CardDefinition[], int)>>(nameof(GetCandidatePoolAsync));

    public virtual Task<IReadOnlyDictionary<CardRole, string[]>> GetLegalCardsByRoleAsync(
        IReadOnlySet<ManaColor> commanderColors, int perRoleLimit, decimal? maxUsd = null) =>
        NotStubbed<Task<IReadOnlyDictionary<CardRole, string[]>>>(nameof(GetLegalCardsByRoleAsync));

    public virtual Task<CardDefinition[]> SearchCommandersAsync(
        string? nameQuery, int limit = 100, string? setCode = null) =>
        NotStubbed<Task<CardDefinition[]>>(nameof(SearchCommandersAsync));
}

/// <inheritdoc cref="StubScryfallService"/>
public abstract class StubSynergyService : ISynergyService
{
    private static T NotStubbed<T>(string member) =>
        throw new NotSupportedException($"{member} was called but this test did not stub it.");

    public virtual Task<SynergyResultDto> GetSynergyAsync(SynergyRequest request) =>
        NotStubbed<Task<SynergyResultDto>>(nameof(GetSynergyAsync));

    public virtual Task<DeckScoreDto> ScoreDeckAsync(Guid deckId, string userId) =>
        NotStubbed<Task<DeckScoreDto>>(nameof(ScoreDeckAsync));

    public virtual Task<ScoredCardDto[]> ScoreCardsAsync(
        string commanderOracleId, IReadOnlyList<string> cardOracleIds,
        ScoringMode mode = ScoringMode.Ideal, DeckProfile? profile = null,
        IReadOnlyList<string>? focus = null) =>
        NotStubbed<Task<ScoredCardDto[]>>(nameof(ScoreCardsAsync));
}

/// <inheritdoc cref="StubScryfallService"/>
public abstract class StubCollectionService : ICollectionService
{
    private static T NotStubbed<T>(string member) =>
        throw new NotSupportedException($"{member} was called but this test did not stub it.");

    public virtual Task<CollectionDto[]> GetUserCollectionsAsync(string userId) =>
        NotStubbed<Task<CollectionDto[]>>(nameof(GetUserCollectionsAsync));

    public virtual Task<CollectionDetailDto?> GetCollectionAsync(Guid collectionId, string userId) =>
        NotStubbed<Task<CollectionDetailDto?>>(nameof(GetCollectionAsync));

    public virtual Task<string[]> GetOwnedOracleIdsAsync(string userId, CancellationToken ct = default) =>
        NotStubbed<Task<string[]>>(nameof(GetOwnedOracleIdsAsync));

    public virtual Task<CollectionDetailDto> CreateCollectionAsync(string userId, CreateCollectionRequest request) =>
        NotStubbed<Task<CollectionDetailDto>>(nameof(CreateCollectionAsync));

    public virtual Task<CollectionDetailDto> UpdateCollectionAsync(
        Guid collectionId, string userId, UpdateCollectionRequest request) =>
        NotStubbed<Task<CollectionDetailDto>>(nameof(UpdateCollectionAsync));

    public virtual Task<bool> DeleteCollectionAsync(Guid collectionId, string userId) =>
        NotStubbed<Task<bool>>(nameof(DeleteCollectionAsync));

    public virtual Task<(CollectionCardDto Card, bool Created)> AddCardToCollectionAsync(
        Guid collectionId, string userId, AddCardToCollectionRequest request) =>
        NotStubbed<Task<(CollectionCardDto, bool)>>(nameof(AddCardToCollectionAsync));

    public virtual Task<CollectionCardDto> UpdateCollectionCardAsync(
        Guid collectionId, Guid cardId, string userId, UpdateCollectionCardRequest request) =>
        NotStubbed<Task<CollectionCardDto>>(nameof(UpdateCollectionCardAsync));

    public virtual Task<bool> RemoveCardFromCollectionAsync(Guid collectionId, Guid cardId, string userId) =>
        NotStubbed<Task<bool>>(nameof(RemoveCardFromCollectionAsync));

    public virtual Task<bool> RemoveCardByOracleAsync(
        Guid collectionId, string oracleId, string userId, string? board = null) =>
        NotStubbed<Task<bool>>(nameof(RemoveCardByOracleAsync));

    public virtual Task<MoveCardResultDto> MoveCardAsync(
        Guid collectionId, Guid cardId, string userId, MoveCardRequest request, CancellationToken ct = default) =>
        NotStubbed<Task<MoveCardResultDto>>(nameof(MoveCardAsync));

    public virtual Task<MoveCardsResultDto> MoveCardsAsync(
        Guid collectionId, string userId, MoveCardsRequest request, CancellationToken ct = default) =>
        NotStubbed<Task<MoveCardsResultDto>>(nameof(MoveCardsAsync));

    public virtual Task<MergeCollectionsResultDto> MergeCollectionsAsync(
        Guid targetCollectionId, string userId, MergeCollectionsRequest request, CancellationToken ct = default) =>
        NotStubbed<Task<MergeCollectionsResultDto>>(nameof(MergeCollectionsAsync));

    public virtual Task<DeckDto[]> GetUserDecksAsync(string userId) =>
        NotStubbed<Task<DeckDto[]>>(nameof(GetUserDecksAsync));

    public virtual Task<DeckDetailDto?> GetDeckAsync(Guid deckId, string userId) =>
        NotStubbed<Task<DeckDetailDto?>>(nameof(GetDeckAsync));

    public virtual Task<DeckDetailDto> CreateDeckAsync(string userId, CreateDeckRequest request) =>
        NotStubbed<Task<DeckDetailDto>>(nameof(CreateDeckAsync));

    public virtual Task<DeckDetailDto> UpdateDeckAsync(Guid deckId, string userId, UpdateDeckRequest request) =>
        NotStubbed<Task<DeckDetailDto>>(nameof(UpdateDeckAsync));

    public virtual Task<bool> DeleteDeckAsync(Guid deckId, string userId) =>
        NotStubbed<Task<bool>>(nameof(DeleteDeckAsync));
}
