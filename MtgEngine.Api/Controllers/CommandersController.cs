using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Enums;

namespace MtgEngine.Api.Controllers;

/// <summary>
/// Community commander statistics. Browsing is public (the client's community pages
/// have no login gate), so the read actions opt out of the app's deny-by-default
/// policy individually. The one endpoint that spends money — AI suggestions — stays
/// behind authentication and the AI rate limit.
/// </summary>
[ApiController]
[Route("api/commanders")]
public sealed class CommandersController : ControllerBase
{
    private readonly ICommanderStatsService _stats;
    private readonly IDeckSuggestionsService _suggestions;
    private readonly IScryfallService _scryfall;

    public CommandersController(
        ICommanderStatsService stats,
        IDeckSuggestionsService suggestions,
        IScryfallService scryfall)
    {
        _stats = stats;
        _suggestions = suggestions;
        _scryfall = scryfall;
    }

    /// <summary>Top commanders ranked by number of community published decks.</summary>
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<CommanderSummaryDto[]>> GetTopCommanders(
        [FromQuery] int limit = 50,
        [FromQuery] int sinceMonths = 0)
    {
        limit = Math.Clamp(limit, 1, 200);
        sinceMonths = Math.Clamp(sinceMonths, 0, 24);
        var result = await _stats.GetTopCommandersAsync(limit, sinceMonths);
        return Ok(result);
    }

    /// <summary>
    /// Searches every Commander-legal commander by name.
    /// </summary>
    /// <remarks>
    /// The ranked list at GET /api/commanders only contains commanders that already have
    /// a published deck here -- currently a few dozen -- so searching it misses almost
    /// everything. This searches the full card corpus and merges deck counts in, so a
    /// commander with no decks yet still shows up (with a count of zero).
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("search")]
    public async Task<ActionResult<CommanderSummaryDto[]>> SearchCommanders(
        [FromQuery] string? q = null,
        [FromQuery] int limit = 100,
        [FromQuery] string? set = null)
    {
        limit = Math.Clamp(limit, 1, 200);

        var matches = await _scryfall.SearchCommandersAsync(q, limit, set);
        if (matches.Length == 0)
            return Ok(Array.Empty<CommanderSummaryDto>());

        // Deck counts come from the ranked list; anything not in it simply has none.
        var ranked = await _stats.GetTopCommandersAsync(limit: 200);
        var counts = ranked.ToDictionary(c => c.OracleId, c => c.DeckCount, StringComparer.OrdinalIgnoreCase);

        var results = matches
            .Select(d => new
            {
                Def = d,
                Count = counts.TryGetValue(d.OracleId, out var n) ? n : 0,
            })
            // Popular commanders first, then alphabetical, so the list is stable.
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Def.Name, StringComparer.OrdinalIgnoreCase)
            .Select((x, i) => new CommanderSummaryDto
            {
                OracleId = x.Def.OracleId,
                Name = x.Def.Name,
                ImageUri = x.Def.ImageUriNormal,
                ImageUriArtCrop = x.Def.ImageUriArtCrop,
                ColorIdentity = [.. x.Def.ColorIdentity.Select(c => c switch
                {
                    ManaColor.White => "W",
                    ManaColor.Blue => "U",
                    ManaColor.Black => "B",
                    ManaColor.Red => "R",
                    ManaColor.Green => "G",
                    _ => "C",
                })],
                ManaCost = string.IsNullOrEmpty(x.Def.ManaCostRaw) ? null : x.Def.ManaCostRaw,
                DeckCount = x.Count,
                Rank = i + 1,
            })
            .ToArray();

        return Ok(results);
    }

    /// <summary>Profile + community deck count + top strategy tags for one commander.</summary>
    [AllowAnonymous]
    [HttpGet("{oracleId}")]
    public async Task<ActionResult<CommanderProfileDto>> GetCommanderProfile(string oracleId)
    {
        var profile = await _stats.GetCommanderProfileAsync(oracleId);
        if (profile == null)
            return NotFound();
        return Ok(profile);
    }

    /// <summary>Top cards used in community decks with this commander, sorted by inclusion %.</summary>
    [AllowAnonymous]
    [HttpGet("{oracleId}/cards")]
    public async Task<ActionResult<CommanderCardsDto>> GetCommanderCards(
        string oracleId,
        [FromQuery] int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 200);
        var result = await _stats.GetCommanderCardsAsync(oracleId, limit);
        return Ok(result);
    }

    /// <summary>Monthly deck-count history for a commander (last N months).</summary>
    [AllowAnonymous]
    [HttpGet("{oracleId}/history")]
    public async Task<ActionResult<CommanderHistoryPointDto[]>> GetCommanderHistory(
        string oracleId,
        [FromQuery] int months = 12)
    {
        months = Math.Clamp(months, 1, 36);
        var result = await _stats.GetCommanderHistoryAsync(oracleId, months);
        return Ok(result);
    }

    /// <summary>Commanders whose decks share the most cards with this commander.</summary>
    [AllowAnonymous]
    [HttpGet("{oracleId}/similar")]
    public async Task<ActionResult<SimilarCommanderDto[]>> GetSimilarCommanders(
        string oracleId,
        [FromQuery] int limit = 6)
    {
        limit = Math.Clamp(limit, 1, 20);
        var result = await _stats.GetSimilarCommandersAsync(oracleId, limit);
        return Ok(result);
    }

    /// <summary>Community decks published for a commander, newest first.</summary>
    [AllowAnonymous]
    [HttpGet("{oracleId}/decks")]
    public async Task<ActionResult<CommanderDeckDto[]>> GetCommanderDecks(
        string oracleId,
        [FromQuery] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);
        var result = await _stats.GetCommanderDecksAsync(oracleId, limit);
        return Ok(result);
    }

    /// <summary>AI-powered card suggestions for a commander (no specific deck required).</summary>
    [Authorize]
    [EnableRateLimiting("ai")]
    [HttpGet("{oracleId}/suggestions")]
    public async Task<ActionResult<DeckSuggestionsDto>> GetCommanderSuggestions(string oracleId)
    {
        var card = await _scryfall.GetByOracleIdAsync(oracleId);
        // Only cards that can actually head a deck get the pipeline — otherwise every
        // oracle id in the corpus is a distinct paid-generation cache key.
        if (card == null || !CommanderRules.IsCommanderEligible(card))
            return NotFound();

        var request = new DeckSuggestionsRequest
        {
            CommanderOracleId = oracleId,
            CommanderName = card.Name,
            CommanderText = card.OracleText,
            DeckCardNames = [],
        };

        var suggestions = await _suggestions.GetSuggestionsAsync(request);
        return Ok(suggestions);
    }
}
