using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public sealed class DecksController : ControllerBase
{
    private readonly ICollectionService _service;

    private string UserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("User ID claim missing from token");

    public DecksController(ICollectionService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<DeckDto[]>> GetDecks()
    {
        var decks = await _service.GetUserDecksAsync(UserId);
        return Ok(decks);
    }

    [HttpGet("{deckId:guid}")]
    public async Task<ActionResult<DeckDetailDto>> GetDeck(Guid deckId)
    {
        var deck = await _service.GetDeckAsync(deckId, UserId);
        if (deck == null) return NotFound();
        return Ok(deck);
    }

    [HttpPost]
    public async Task<ActionResult<DeckDetailDto>> CreateDeck([FromBody] CreateDeckRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Deck name is required");

        var deck = await _service.CreateDeckAsync(UserId, request);
        return CreatedAtAction(nameof(GetDeck), new { deckId = deck.Id }, deck);
    }

    [HttpPut("{deckId:guid}")]
    public async Task<ActionResult<DeckDetailDto>> UpdateDeck(Guid deckId, [FromBody] UpdateDeckRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Deck name is required");

        try
        {
            var deck = await _service.UpdateDeckAsync(deckId, UserId, request);
            return Ok(deck);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("import")]
    public async Task<ActionResult<ImportDeckResult>> ImportDeck(
        [FromBody] ImportDeckRequest request,
        [FromServices] DeckImportService importer)
    {
        if (string.IsNullOrWhiteSpace(request.Text) && string.IsNullOrWhiteSpace(request.Url))
            return BadRequest(new { message = "Either 'text' or 'url' is required." });
        try
        {
            var result = await importer.ImportAsync(UserId, request);
            return Ok(result);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        catch (HttpRequestException ex)      { return BadRequest(new { message = $"Failed to fetch deck: {ex.Message}" }); }
    }

    [HttpDelete("{deckId:guid}")]
    public async Task<ActionResult> DeleteDeck(Guid deckId)
    {
        var success = await _service.DeleteDeckAsync(deckId, UserId);
        if (!success) return NotFound();
        return NoContent();
    }

    // ---- Card management (reuses collection card endpoints) ----

    [HttpPost("{deckId:guid}/cards")]
    public async Task<ActionResult<CollectionCardDto>> AddCard(
        Guid deckId,
        [FromBody] AddCardToCollectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OracleId))
            return BadRequest("OracleId is required");

        if (request.Quantity < 0 || request.QuantityFoil < 0 || request.Quantity + request.QuantityFoil < 1)
            return BadRequest("Total quantity must be at least 1");

        try
        {
            var card = await _service.AddCardToCollectionAsync(deckId, UserId, request);
            return CreatedAtAction(nameof(GetCard), new { deckId, cardId = card.Id }, card);
        }
        catch (KeyNotFoundException)
        {
            return NotFound("Deck not found");
        }
    }

    [HttpGet("{deckId:guid}/cards/{cardId:guid}")]
    public async Task<ActionResult<CollectionCardDto>> GetCard(Guid deckId, Guid cardId)
    {
        var card = await _service.GetCollectionCardAsync(deckId, cardId, UserId);
        if (card == null) return NotFound();
        return Ok(card);
    }

    [HttpPut("{deckId:guid}/cards/{cardId:guid}")]
    public async Task<ActionResult<CollectionCardDto>> UpdateCard(
        Guid deckId,
        Guid cardId,
        [FromBody] UpdateCollectionCardRequest request)
    {
        if (request.Quantity < 0 || request.QuantityFoil < 0 || request.Quantity + request.QuantityFoil < 1)
            return BadRequest("Total quantity must be at least 1");

        try
        {
            var card = await _service.UpdateCollectionCardAsync(deckId, cardId, UserId, request);
            return Ok(card);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpDelete("{deckId:guid}/cards/{cardId:guid}")]
    public async Task<ActionResult> RemoveCard(Guid deckId, Guid cardId)
    {
        var success = await _service.RemoveCardFromCollectionAsync(deckId, cardId, UserId);
        if (!success) return NotFound();
        return NoContent();
    }

    // ---- Deck suggestions ------------------------------------------

    [HttpPost("suggestions")]
    public async Task<ActionResult<DeckSuggestionsDto>> GetSuggestions(
        [FromBody] DeckSuggestionsRequest request,
        [FromServices] IDeckSuggestionsService suggestionsService)
    {
        if (string.IsNullOrWhiteSpace(request.CommanderOracleId))
            return BadRequest("CommanderOracleId is required");

        try
        {
            var result = await suggestionsService.GetSuggestionsAsync(request);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { message = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { message = $"LLM API error: {ex.Message}" });
        }
    }

    // ---- Synergy scoring -------------------------------------------

    [HttpPost("synergy")]
    public async Task<ActionResult<SynergyResultDto>> GetSynergy(
        [FromBody] SynergyRequest request,
        [FromServices] ISynergyService synergyService)
    {
        if (string.IsNullOrWhiteSpace(request.CommanderOracleId) || string.IsNullOrWhiteSpace(request.CardOracleId))
            return BadRequest("CommanderOracleId and CardOracleId are required");

        try
        {
            var result = await synergyService.GetSynergyAsync(request);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { message = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { message = $"LLM API error: {ex.Message}" });
        }
    }
}
