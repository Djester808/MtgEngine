using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

/// <summary>
/// Manages user card collections and deck building from owned cards.
/// </summary>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public sealed class CollectionsController : ControllerBase
{
    private readonly ICollectionService _collectionService;

    private string UserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("User ID claim missing from token");

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
        var collections = await _collectionService.GetUserCollectionsAsync(UserId);
        return Ok(collections);
    }

    /// <summary>
    /// Get a specific collection with all its cards.
    /// </summary>
    [HttpGet("{collectionId:guid}")]
    public async Task<ActionResult<CollectionDetailDto>> GetCollection(Guid collectionId)
    {
        var collection = await _collectionService.GetCollectionAsync(collectionId, UserId);
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
            return Problem(detail: "Collection name is required", statusCode: StatusCodes.Status400BadRequest);

        var collection = await _collectionService.CreateCollectionAsync(UserId, request);
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
            return Problem(detail: "Collection name is required", statusCode: StatusCodes.Status400BadRequest);

        try
        {
            var collection = await _collectionService.UpdateCollectionAsync(collectionId, UserId, request);
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
        var success = await _collectionService.DeleteCollectionAsync(collectionId, UserId);
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
            return Problem(detail: "OracleId is required", statusCode: StatusCodes.Status400BadRequest);

        if (request.Quantity < 0 || request.QuantityFoil < 0 || request.Quantity + request.QuantityFoil < 1)
            return Problem(detail: "Total quantity must be at least 1 and neither value may be negative", statusCode: StatusCodes.Status400BadRequest);

        try
        {
            var (card, created) = await _collectionService.AddCardToCollectionAsync(
                collectionId, UserId, request);
            // 201 only for a genuinely new row; incrementing an existing one is a 200.
            return created ? StatusCode(StatusCodes.Status201Created, card) : Ok(card);
        }
        catch (KeyNotFoundException)
        {
            return Problem(detail: "Collection not found", statusCode: StatusCodes.Status404NotFound);
        }
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
            return Problem(detail: "Total quantity must be at least 1 and neither value may be negative", statusCode: StatusCodes.Status400BadRequest);

        try
        {
            var card = await _collectionService.UpdateCollectionCardAsync(
                collectionId, cardId, UserId, request);
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
        var success = await _collectionService.RemoveCardFromCollectionAsync(collectionId, cardId, UserId);
        if (!success)
            return NotFound();

        return NoContent();
    }

}
