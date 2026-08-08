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
