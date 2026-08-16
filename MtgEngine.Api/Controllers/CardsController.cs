using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Mapping;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public sealed class CardsController : ControllerBase
{
    private readonly IScryfallService _scryfall;
    private readonly IPriceHistoryService _priceHistory;
    private readonly ICardHistoryService _cardHistory;

    private string UserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("User ID claim missing from token");

    public CardsController(
        IScryfallService scryfall,
        IPriceHistoryService priceHistory,
        ICardHistoryService cardHistory)
    {
        _scryfall = scryfall;
        _priceHistory = priceHistory;
        _cardHistory = cardHistory;
    }

    /// <summary>Must match DeckSuggestionsService.LatestSetCount so the browse list
    /// covers exactly the sets the "latest" category drew from.</summary>
    private const int LatestSetScopeCount = 3;

    [HttpGet("search")]
    public async Task<ActionResult<CardDto[]>> Search(
        [FromQuery] string q,
        [FromQuery] int limit = 60,
        [FromQuery] int offset = 0,
        [FromQuery] string sortBy = "name",
        [FromQuery] string sortDir = "asc",
        [FromQuery] bool matchCase = false,
        [FromQuery] bool matchWord = false,
        [FromQuery] bool useRegex = false)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Ok(Array.Empty<CardDto>());

        // Clamp like every sibling endpoint — an unclamped limit materializes and
        // serializes the whole ~30k-card corpus in one response.
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);

        var results = await _scryfall.SearchAsync(q, limit, offset, sortBy, sortDir, matchCase, matchWord, useRegex);
        return Ok(results.Select(MapToDto).ToArray());
    }

    /// <summary>
    /// The legal card pool a suggestion category is drawn from, so the user can see more
    /// than the handful the model picked. No model involved: these are the real candidates.
    /// </summary>
    /// <param name="scope">
    /// Which category's pool to browse: "latest" for the newest sets, "gamechangers" for
    /// the official list, anything else for the whole legal pool.
    /// </param>
    /// <param name="focus">
    /// Themes the player asked to build around, comma separated. Scoring without them
    /// answers a different question and produces different percentages.
    /// </param>
    /// <param name="rank">
    /// "synergy" to order by score — the same ordering the suggestion categories use, so
    /// the first page here is exactly the category and scrolling continues the same list.
    /// Anything else keeps relevance order.
    /// </param>
    [HttpGet("candidates")]
    public async Task<ActionResult<CandidatePoolDto>> Candidates(
        [FromQuery] string commanderOracleId,
        [FromServices] ICandidateRanking ranking,
        [FromQuery] string? q = null,
        [FromQuery] string? scope = null,
        [FromQuery] string? types = null,
        [FromQuery] int? cmcMin = null,
        [FromQuery] int? cmcMax = null,
        [FromQuery] string? focus = null,
        [FromQuery] string? rank = null,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        if (string.IsNullOrWhiteSpace(commanderOracleId))
            return Problem(detail: "commanderOracleId is required", statusCode: StatusCodes.Status400BadRequest);

        var commander = await _scryfall.GetByOracleIdAsync(commanderOracleId);
        if (commander is null)
            return NotFound($"No card with oracle id '{commanderOracleId}'");

        IReadOnlySet<string>? setCodes = null;
        bool gameChangersOnly = false;
        if (string.Equals(scope, "latest", StringComparison.OrdinalIgnoreCase))
            setCodes = await _scryfall.GetRecentSetCodesAsync(maxSets: LatestSetScopeCount);
        else if (string.Equals(scope, "gamechangers", StringComparison.OrdinalIgnoreCase))
            gameChangersOnly = true;

        // Unknown type names are ignored rather than rejected: the filter is a
        // convenience, and a typo should not turn the whole browse list into an error.
        var typeFlags = CardType.None;
        foreach (var name in (types ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Enum.TryParse<CardType>(name, ignoreCase: true, out var parsed))
                typeFlags |= parsed;

        var focusTags = (focus ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        CardDefinition[] cards;
        int total;
        Dictionary<string, int> scoreOf = new(StringComparer.OrdinalIgnoreCase);

        if (string.Equals(rank, "synergy", StringComparison.OrdinalIgnoreCase))
        {
            var ranked = await ranking.RankAsync(new RankRequest(
                commander, q, setCodes, gameChangersOnly, typeFlags, cmcMin, cmcMax,
                focusTags, Math.Clamp(limit, 1, 200), Math.Max(0, offset)));

            cards = [.. ranked.Cards.Select(c => c.Card)];
            total = ranked.Total;
            foreach (var c in ranked.Cards)
                scoreOf[c.Card.OracleId] = c.Score;
        }
        else
        {
            (cards, total) = await _scryfall.GetCandidatePoolAsync(
                commander.ColorIdentity.ToHashSet(), commander, q, setCodes, gameChangersOnly,
                typeFlags, cmcMin, cmcMax,
                Math.Clamp(limit, 1, 200), Math.Max(0, offset));
        }

        var rows = await Task.WhenAll(cards.Select(async d =>
        {
            var printings = await _scryfall.GetPrintingsAsync(d.OracleId);

            // Prefer the printing from the set being browsed, so a card reprinted into
            // the latest set is labelled with that set rather than its oldest one.
            var printing = printings.FirstOrDefault(p =>
                    p.SetCode is not null && setCodes?.Contains(p.SetCode) == true)
                ?? printings.FirstOrDefault();

            // Ranked responses carry the score, so the client renders the same number it
            // was ordered by instead of fetching it again and risking a different answer.
            return new CandidateCardDto(
                MapToDto(d), printing?.ScryfallId, printing?.SetCode?.ToUpperInvariant(), printing?.SetName,
                scoreOf.TryGetValue(d.OracleId, out var score) && score >= 0 ? score : null);
        }));

        return Ok(new CandidatePoolDto(total, rows));
    }

    [HttpGet("by-name")]
    public async Task<ActionResult<CardDto>> GetByName([FromQuery] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Problem(detail: "name is required", statusCode: StatusCodes.Status400BadRequest);

        var trimmed = name.Trim();
        var card = await _scryfall.GetByNameAsync(trimmed);
        if (card is not null)
            return Ok(MapToDto(card));

        // Fallback: bulk search with the name as-is (handles cases where fuzzy live lookup fails)
        var results = await _scryfall.SearchAsync(trimmed, 1);
        if (results.Length > 0)
            return Ok(MapToDto(results[0]));

        return NotFound();
    }

    [HttpGet("sets")]
    public async Task<ActionResult<SetSummaryDto[]>> GetSets([FromQuery] string? q = null)
    {
        var sets = await _scryfall.GetSetsAsync(q);
        return Ok(sets);
    }

    [HttpGet("{oracleId}")]
    public async Task<ActionResult<CardDto>> GetCard(string oracleId)
    {
        var card = await _scryfall.GetByOracleIdAsync(oracleId);
        if (card is null)
            return NotFound();
        return Ok(MapToDto(card));
    }

    [HttpGet("{oracleId}/printings")]
    public async Task<ActionResult<PrintingDto[]>> GetPrintings(string oracleId)
    {
        var printings = await _scryfall.GetPrintingsAsync(oracleId);
        return Ok(printings);
    }

    [HttpGet("{oracleId}/rulings")]
    public async Task<ActionResult<RulingDto[]>> GetRulings(string oracleId)
    {
        var rulings = await _scryfall.GetRulingsAsync(oracleId);
        return Ok(rulings);
    }

    /// <summary>
    /// Daily price history for one printing. Empty until the printing has spent time in
    /// a collection — snapshots are only recorded for owned printings.
    /// </summary>
    [HttpGet("printings/{scryfallId}/price-history")]
    public async Task<ActionResult<PricePointDto[]>> GetPriceHistory(
        [StringLength(256)] string scryfallId,
        [FromQuery][Range(1, PriceHistoryService.MaxDays)] int days = 90,
        CancellationToken ct = default)
    {
        return Ok(await _priceHistory.GetHistoryAsync(scryfallId, days, ct));
    }

    /// <summary>
    /// What the current user has done with this card — added, removed, moved between
    /// collections and decks — newest first. Scoped to the caller: this is their activity,
    /// not the card's. Empty until the card is touched after history recording shipped;
    /// nothing reconstructs changes made before that.
    /// </summary>
    [HttpGet("{oracleId}/history")]
    public async Task<ActionResult<CardHistoryEntryDto[]>> GetHistory(
        [StringLength(256)] string oracleId,
        [FromQuery][Range(1, CardHistoryService.MaxLimit)] int limit = 100,
        CancellationToken ct = default)
    {
        return Ok(await _cardHistory.GetForCardAsync(UserId, oracleId, limit, ct));
    }

    /// <summary>Oracle card to DTO. See <see cref="DomainMapper"/>.</summary>
    private static CardDto MapToDto(CardDefinition def) => DomainMapper.ToDto(def);

}
