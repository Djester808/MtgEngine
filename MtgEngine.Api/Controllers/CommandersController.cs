using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

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

    /// <summary>Profile + community deck count + top strategy tags for one commander.</summary>
    [HttpGet("{oracleId}")]
    public async Task<ActionResult<CommanderProfileDto>> GetCommanderProfile(string oracleId)
    {
        var profile = await _stats.GetCommanderProfileAsync(oracleId);
        if (profile == null)
            return NotFound();
        return Ok(profile);
    }

    /// <summary>Top cards used in community decks with this commander, sorted by inclusion %.</summary>
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
    [HttpGet("{oracleId}/suggestions")]
    public async Task<ActionResult<DeckSuggestionsDto>> GetCommanderSuggestions(string oracleId)
    {
        var card = await _scryfall.GetByOracleIdAsync(oracleId);
        if (card == null)
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
