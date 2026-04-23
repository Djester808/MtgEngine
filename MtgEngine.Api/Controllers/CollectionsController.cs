using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

/// <summary>
/// Manages user card collections and deck building from owned cards.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class CollectionsController : ControllerBase
{
    private readonly ICollectionService _collectionService;
    private const string DefaultUserId = "user-default"; // TODO: Replace with actual auth

    public CollectionsController(ICollectionService collectionService)
    {
        _collectionService = collectionService;
    }

    // ---- Collection Management ----

    /// <summary>
    /// Get all collections for the current user.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<CollectionDto[]>> GetCollections()
    {
        var collections = await _collectionService.GetUserCollectionsAsync(DefaultUserId);
        return Ok(collections);
    }

    /// <summary>
    /// Get a specific collection with all its cards.
    /// </summary>
    [HttpGet("{collectionId:guid}")]
    public async Task<ActionResult<CollectionDetailDto>> GetCollection(Guid collectionId)
    {
        var collection = await _collectionService.GetCollectionAsync(collectionId, DefaultUserId);
        if (collection == null)
            return NotFound();

        return Ok(collection);
    }

    /// <summary>
    /// Create a new collection.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<CollectionDetailDto>> CreateCollection(
        [FromBody] CreateCollectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Collection name is required");

        var collection = await _collectionService.CreateCollectionAsync(DefaultUserId, request);
        return CreatedAtAction(nameof(GetCollection), new { collectionId = collection.Id }, collection);
    }

    /// <summary>
    /// Update a collection's metadata.
    /// </summary>
    [HttpPut("{collectionId:guid}")]
    public async Task<ActionResult<CollectionDetailDto>> UpdateCollection(
        Guid collectionId,
        [FromBody] UpdateCollectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Collection name is required");

        try
        {
            var collection = await _collectionService.UpdateCollectionAsync(collectionId, DefaultUserId, request);
            return Ok(collection);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Delete a collection.
    /// </summary>
    [HttpDelete("{collectionId:guid}")]
    public async Task<ActionResult> DeleteCollection(Guid collectionId)
    {
        var success = await _collectionService.DeleteCollectionAsync(collectionId, DefaultUserId);
        if (!success)
            return NotFound();

        return NoContent();
    }

    // ---- Collection Cards ----

    /// <summary>
    /// Add a card to a collection (or increment quantity if already owned).
    /// </summary>
    [HttpPost("{collectionId:guid}/cards")]
    public async Task<ActionResult<CollectionCardDto>> AddCardToCollection(
        Guid collectionId,
        [FromBody] AddCardToCollectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OracleId))
            return BadRequest("OracleId is required");

        if (request.Quantity < 0 || request.QuantityFoil < 0 || request.Quantity + request.QuantityFoil < 1)
            return BadRequest("Total quantity must be at least 1 and neither value may be negative");

        try
        {
            var card = await _collectionService.AddCardToCollectionAsync(collectionId, DefaultUserId, request);
            return CreatedAtAction(nameof(GetCollectionCard), 
                new { collectionId, cardId = card.Id }, card);
        }
        catch (KeyNotFoundException)
        {
            return NotFound("Collection not found");
        }
    }

    /// <summary>
    /// Get a specific card from a collection.
    /// </summary>
    [HttpGet("{collectionId:guid}/cards/{cardId:guid}")]
    public async Task<ActionResult<CollectionCardDto>> GetCollectionCard(Guid collectionId, Guid cardId)
    {
        var card = await _collectionService.GetCollectionCardAsync(collectionId, cardId, DefaultUserId);
        if (card == null)
            return NotFound();

        return Ok(card);
    }

    /// <summary>
    /// Update a card's quantity, foil status, or notes in a collection.
    /// </summary>
    [HttpPut("{collectionId:guid}/cards/{cardId:guid}")]
    public async Task<ActionResult<CollectionCardDto>> UpdateCollectionCard(
        Guid collectionId,
        Guid cardId,
        [FromBody] UpdateCollectionCardRequest request)
    {
        if (request.Quantity < 0 || request.QuantityFoil < 0 || request.Quantity + request.QuantityFoil < 1)
            return BadRequest("Total quantity must be at least 1 and neither value may be negative");

        try
        {
            var card = await _collectionService.UpdateCollectionCardAsync(
                collectionId, cardId, DefaultUserId, request);
            return Ok(card);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Remove a card from a collection by its ID.
    /// </summary>
    [HttpDelete("{collectionId:guid}/cards/{cardId:guid}")]
    public async Task<ActionResult> RemoveCardFromCollection(Guid collectionId, Guid cardId)
    {
        var success = await _collectionService.RemoveCardFromCollectionAsync(collectionId, cardId, DefaultUserId);
        if (!success)
            return NotFound();

        return NoContent();
    }

    /// <summary>
    /// Remove all copies of a card (by OracleId) from a collection.
    /// </summary>
    [HttpDelete("{collectionId:guid}/cards/by-oracle/{oracleId}")]
    public async Task<ActionResult> RemoveCardByOracle(Guid collectionId, string oracleId)
    {
        var success = await _collectionService.RemoveCardByOracleAsync(collectionId, oracleId, DefaultUserId);
        if (!success)
            return NotFound();

        return NoContent();
    }

    // ---- Deck Building ----

    /// <summary>
    /// Get all cards from a collection that can be used to build a deck.
    /// </summary>
    [HttpGet("{collectionId:guid}/deck-cards")]
    public async Task<ActionResult<CardDto[]>> GetDeckCards(Guid collectionId)
    {
        var cards = await _collectionService.GetAvailableCardsForDeckAsync(collectionId, DefaultUserId);
        return Ok(cards);
    }
}
