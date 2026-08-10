using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
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

    public CardsController(IScryfallService scryfall) => _scryfall = scryfall;

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
    [HttpGet("candidates")]
    public async Task<ActionResult<CandidatePoolDto>> Candidates(
        [FromQuery] string commanderOracleId,
        [FromQuery] string? q = null,
        [FromQuery] string? scope = null,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        if (string.IsNullOrWhiteSpace(commanderOracleId))
            return BadRequest("commanderOracleId is required");

        var commander = await _scryfall.GetByOracleIdAsync(commanderOracleId);
        if (commander is null)
            return NotFound($"No card with oracle id '{commanderOracleId}'");

        IReadOnlySet<string>? setCodes = null;
        bool gameChangersOnly = false;
        if (string.Equals(scope, "latest", StringComparison.OrdinalIgnoreCase))
            setCodes = await _scryfall.GetRecentSetCodesAsync(maxSets: LatestSetScopeCount);
        else if (string.Equals(scope, "gamechangers", StringComparison.OrdinalIgnoreCase))
            gameChangersOnly = true;

        var (cards, total) = await _scryfall.GetCandidatePoolAsync(
            commander.ColorIdentity.ToHashSet(), q, setCodes, gameChangersOnly,
            Math.Clamp(limit, 1, 200), Math.Max(0, offset));

        var rows = await Task.WhenAll(cards.Select(async d =>
        {
            var printings = await _scryfall.GetPrintingsAsync(d.OracleId);
            return new CandidateCardDto(MapToDto(d), printings.FirstOrDefault()?.ScryfallId);
        }));

        return Ok(new CandidatePoolDto(total, rows));
    }

    [HttpGet("by-name")]
    public async Task<ActionResult<CardDto>> GetByName([FromQuery] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest("name is required");

        var trimmed = name.Trim();
        var card = await _scryfall.GetByNameAsync(trimmed);
        if (card is not null) return Ok(MapToDto(card));

        // Fallback: bulk search with the name as-is (handles cases where fuzzy live lookup fails)
        var results = await _scryfall.SearchAsync(trimmed, 1);
        if (results.Length > 0) return Ok(MapToDto(results[0]));

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
        if (card is null) return NotFound();
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

    private static CardDto MapToDto(CardDefinition def) => new()
    {
        CardId = def.OracleId,
        OracleId = def.OracleId,
        Name = def.Name,
        ManaCost = string.IsNullOrEmpty(def.ManaCostRaw) ? def.ManaCost.ToString() : def.ManaCostRaw,
        ManaValue = def.Cmc,
        CardTypes = def.CardTypes.ToString().Split(", ")
            .Where(t => Enum.IsDefined(typeof(CardTypeDto), t))
            .Select(t => Enum.Parse<CardTypeDto>(t))
            .ToArray(),
        Subtypes = [.. def.Subtypes],
        Supertypes = [.. def.Supertypes],
        OracleText = def.OracleText,
        Power = def.Power,
        Toughness = def.Toughness,
        StartingLoyalty = def.StartingLoyalty,
        Keywords = def.Keywords.ToString().Split(", ")
            .Where(k => !string.IsNullOrEmpty(k) && k != "None")
            .ToArray(),
        ImageUriNormal = def.ImageUriNormal,
        ImageUriLarge = def.ImageUriLarge,
        ImageUriNormalBack = def.ImageUriNormalBack,
        ImageUriSmall = def.ImageUriSmall,
        ImageUriArtCrop = def.ImageUriArtCrop,
        ColorIdentity = def.ColorIdentity
            .Select(c => c switch
            {
                ManaColor.White => ManaColorDto.W,
                ManaColor.Blue => ManaColorDto.U,
                ManaColor.Black => ManaColorDto.B,
                ManaColor.Red => ManaColorDto.R,
                ManaColor.Green => ManaColorDto.G,
                _ => ManaColorDto.C,
            })
            .ToArray(),
        FlavorText = def.FlavorText,
        Artist = def.Artist,
        SetCode = def.SetCode,
        Rarity = def.Rarity,
        Legalities = def.Legalities.ToDictionary(kv => kv.Key, kv => kv.Value),
        GameChanger = def.GameChanger,
    };
}
