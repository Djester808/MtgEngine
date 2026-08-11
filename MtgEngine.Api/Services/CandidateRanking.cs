using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>
/// One ranked list of candidates per category, ordered by synergy score.
/// </summary>
/// <remarks>
/// Selection and scoring used to be separate: a model picked the cards, and a different
/// engine scored them afterwards. The two disagreed visibly -- a category could show a 76%
/// card while the pool it was drawn from held six cards at 80-88%. Ranking here means the
/// suggestion list and the "show more" list are the same list, so the first page of one is
/// the head of the other and the percentages agree by construction.
/// </remarks>
public interface ICandidateRanking
{
    Task<RankedPool> RankAsync(RankRequest request);

    /// <summary>
    /// Ranks several pools that share a commander, scoring the union of their shortlists in
    /// one pass.
    /// </summary>
    /// <remarks>
    /// Ranking four categories one at a time meant four separate scoring runs over pools
    /// that overlap heavily — the same card scored up to four times, each run waiting for
    /// the last. Collapsing them into a single fan-out is most of the difference between a
    /// twenty-second wait and a five-second one.
    /// </remarks>
    Task<IReadOnlyList<RankedPool>> RankManyAsync(IReadOnlyList<RankRequest> requests);
}

/// <param name="ScoreWindow">
/// How deep into the relevance-ordered pool to score before ranking. Scoring every legal
/// card is not affordable, so the deterministic relevance order picks the shortlist and the
/// model ranks within it.
/// </param>
public sealed record RankRequest(
    CardDefinition Commander,
    string? Query = null,
    IReadOnlySet<string>? SetCodes = null,
    bool GameChangersOnly = false,
    CardType Types = CardType.None,
    int? CmcMin = null,
    int? CmcMax = null,
    IReadOnlyList<string>? Focus = null,
    int Limit = 8,
    int Offset = 0,
    int ScoreWindow = CandidateRanking.DefaultScoreWindow);

/// <param name="Total">Size of the whole pool, not just the scored window.</param>
/// <param name="Scored">How many of the pool were scored and ranked.</param>
public sealed record RankedPool(
    IReadOnlyList<RankedCard> Cards,
    int Total,
    int Scored);

public sealed record RankedCard(CardDefinition Card, int Score, string Reason);

public sealed class CandidateRanking : ICandidateRanking
{
    /// <summary>
    /// Deep enough that the top of the list is genuinely the best of the pool, shallow
    /// enough to stay affordable: at a chunk size of 8 this is a handful of parallel calls,
    /// and every one of them is cached afterwards.
    /// </summary>
    public const int DefaultScoreWindow = 60;

    private readonly IScryfallService _scryfall;
    private readonly ISynergyService _synergy;
    private readonly ILogger<CandidateRanking> _logger;

    public CandidateRanking(
        IScryfallService scryfall, ISynergyService synergy, ILogger<CandidateRanking> logger)
    {
        _scryfall = scryfall;
        _synergy = synergy;
        _logger = logger;
    }

    /// <summary>One request is just the batch path with a single entry; there is one implementation.</summary>
    public async Task<RankedPool> RankAsync(RankRequest request) =>
        (await RankManyAsync([request]))[0];

    public async Task<IReadOnlyList<RankedPool>> RankManyAsync(IReadOnlyList<RankRequest> requests)
    {
        if (requests.Count == 0) return [];

        // Stage 1: every shortlist. Relevance ordering is in-memory, so this is free.
        var shortlists = new List<(RankRequest Req, CardDefinition[] Cards, int Total)>(requests.Count);
        foreach (var r in requests)
        {
            var window = Math.Clamp(r.ScoreWindow, r.Limit, 200);
            var (cards, total) = await _scryfall.GetCandidatePoolAsync(
                r.Commander.ColorIdentity.ToHashSet(), r.Commander, r.Query, r.SetCodes,
                r.GameChangersOnly, r.Types, r.CmcMin, r.CmcMax, window, offset: 0);

            shortlists.Add((r, cards, total));
        }

        // Stage 2: one scoring pass over the union. The pools overlap, so this is far less
        // work than the sum of the parts, and it is a single fan-out rather than four.
        var union = shortlists
            .SelectMany(s => s.Cards)
            .Select(c => c.OracleId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var first = requests[0];
        var scores = await _synergy.ScoreCardsAsync(
            first.Commander.OracleId, union, ScoringMode.Ideal, profile: null, focus: first.Focus);

        var byId = scores.ToDictionary(s => s.OracleId, StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(
            "Ranked {Categories} categories for {Commander} from {Union} unique candidates " +
            "({Sum} before de-duplication)",
            requests.Count, first.Commander.Name, union.Length, shortlists.Sum(s => s.Cards.Length));

        return [.. shortlists.Select(s => Rank(s.Req, s.Cards, s.Total, byId))];
    }

    private static RankedPool Rank(
        RankRequest r, CardDefinition[] shortlist, int total, Dictionary<string, ScoredCardDto> byId)
    {
        var ranked = shortlist
            .Select(c => byId.TryGetValue(c.OracleId, out var s)
                ? new RankedCard(c, s.Score, s.Reason)
                : new RankedCard(c, -1, string.Empty))
            .OrderByDescending(x => x.Score)
            .ToArray();

        return new RankedPool(
            [.. ranked.Skip(r.Offset).Take(r.Limit)],
            Math.Min(total, ranked.Length),
            ranked.Length);
    }

}
