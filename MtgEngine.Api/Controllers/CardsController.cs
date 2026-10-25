using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Mapping;
using MtgEngine.Api.Services;
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
        [FromServices] CandidateBrowseService browse,
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
        return Ok(await browse.BrowseAsync(commanderOracleId, q, scope, types, cmcMin, cmcMax, focus, rank, limit, offset));
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
