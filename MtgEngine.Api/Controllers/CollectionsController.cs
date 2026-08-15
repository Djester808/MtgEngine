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
    /// Every oracle id the current user owns a copy of, across their collections. Decks
    /// are not collections for this purpose — the deck grid asks this to tell the cards
    /// you own from the ones you have only listed.
    /// </summary>
    [HttpGet("owned-oracle-ids")]
    public async Task<ActionResult<string[]>> GetOwnedOracleIds(CancellationToken ct)
    {
        var ids = await _collectionService.GetOwnedOracleIdsAsync(UserId, ct);
        return Ok(ids);
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

    /// <summary>
    /// Move copies of a card into another collection. Omit the quantities to move the
    /// whole row. Errors surface through <see cref="AiExceptionHandler"/>: an unknown or
    /// unowned collection is a 404, an impossible move a 409.
    /// </summary>
    [HttpPost("{collectionId:guid}/cards/{cardId:guid}/move")]
    public async Task<ActionResult<MoveCardResultDto>> MoveCard(
        Guid collectionId,
        Guid cardId,
        [FromBody] MoveCardRequest request,
        CancellationToken ct)
    {
        return Ok(await _collectionService.MoveCardAsync(collectionId, cardId, UserId, request, ct));
    }

    /// <summary>
    /// Move several whole card rows into another collection in one request.
    /// </summary>
    [HttpPost("{collectionId:guid}/cards/move")]
    public async Task<ActionResult<MoveCardsResultDto>> MoveCards(
        Guid collectionId,
        [FromBody] MoveCardsRequest request,
        CancellationToken ct)
    {
        return Ok(await _collectionService.MoveCardsAsync(collectionId, UserId, request, ct));
    }

    /// <summary>
    /// Merge another collection into this one, folding matching printings together.
    /// </summary>
    [HttpPost("{collectionId:guid}/merge")]
    public async Task<ActionResult<MergeCollectionsResultDto>> MergeCollections(
        Guid collectionId,
        [FromBody] MergeCollectionsRequest request,
        CancellationToken ct)
    {
        return Ok(await _collectionService.MergeCollectionsAsync(collectionId, UserId, request, ct));
    }
}
