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
    private readonly CollectionMutationUndoService _undo;

    private string UserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("User ID claim missing from token");

    public CollectionsController(ICollectionService collectionService, CollectionMutationUndoService undo)
    {
        _collectionService = collectionService;
        _undo = undo;
    }

    private T OfferUndo<T>(UndoableChange<T> change, Guid[] collections, string kind) => CollectionUndoHeaders.Apply(Response, change, collections, kind);

    [HttpGet("{collectionId:guid}/export")]
    public async Task<IActionResult> Export(Guid collectionId, [FromServices] CollectionExportService exports, CancellationToken ct, [FromQuery] string format = "csv")
    {
        var file = await exports.ExportAsync(collectionId, UserId, format.ToLowerInvariant(), ct);
        Response.Headers.CacheControl = "no-store";
        return File(System.Text.Encoding.UTF8.GetBytes(file.Content), file.ContentType, file.FileName);
    }

    [HttpGet("owned")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<OwnedPrinting[]>> GetOwned([FromServices] CollectionOwnershipService ownership, CancellationToken ct) =>
        Ok(await ownership.GetAsync(UserId, ct).ConfigureAwait(false));

    [HttpPost("{collectionId:guid}/query")]
    public async Task<ActionResult<CollectionDetailDto>> QueryCollection(Guid collectionId,
        [FromBody] CollectionPageQuery query, [FromServices] CollectionReadService reads, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var page = await reads.PageAsync(collectionId, UserId, query, ct);
        return page is null ? NotFound() : Ok(page);
    }

    [HttpPost("{collectionId:guid}/rows")]
    public async Task<ActionResult<CollectionCardDto[]>> LookupCollectionRows(Guid collectionId,
        [FromBody] string[] oracleIds, [FromServices] CollectionReadService reads, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await reads.LookupAsync(collectionId, UserId, oracleIds, ct));
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
            return Ok(collection with { Cards = [] });
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
        [FromBody] AddCardToCollectionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OracleId))
            return Problem(detail: "OracleId is required", statusCode: StatusCodes.Status400BadRequest);

        if (request.Quantity < 0 || request.QuantityFoil < 0 || request.Quantity + request.QuantityFoil + (request.QuantityEtched ?? 0) < 1)
            return Problem(detail: "Total quantity must be at least 1 and neither value may be negative", statusCode: StatusCodes.Status400BadRequest);

        try
        {
            var change = await _undo.RunAsync(UserId, [collectionId], [request.OracleId], () => _collectionService.AddCardToCollectionAsync(collectionId, UserId, request), true, ct);
            var (card, created) = OfferUndo(change, [collectionId], "add");
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
        [FromBody] UpdateCollectionCardRequest request, CancellationToken ct)
    {
        if (request.Quantity < 0 || request.QuantityFoil < 0 || request.Quantity + request.QuantityFoil + (request.QuantityEtched ?? 0) < 1)
            return Problem(detail: "Total quantity must be at least 1 and neither value may be negative", statusCode: StatusCodes.Status400BadRequest);

        try
        {
            var oracles = await _undo.OraclesAsync(collectionId, UserId, [cardId], ct);
            var change = await _undo.RunAsync(UserId, [collectionId], oracles, () => _collectionService.UpdateCollectionCardAsync(collectionId, cardId, UserId, request), false, ct);
            var card = OfferUndo(change, [collectionId], "edit");
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
    public async Task<ActionResult> RemoveCardFromCollection(Guid collectionId, Guid cardId, CancellationToken ct)
    {
        var oracles = await _undo.OraclesAsync(collectionId, UserId, [cardId], ct);
        var change = await _undo.RunAsync(UserId, [collectionId], oracles, () => _collectionService.RemoveCardFromCollectionAsync(collectionId, cardId, UserId), false, ct);
        var success = OfferUndo(change, [collectionId], "remove");
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
        var oracles = await _undo.OraclesAsync(collectionId, UserId, [cardId], ct);
        var ids = new[] { collectionId, request.TargetCollectionId };
        var change = await _undo.RunAsync(UserId, ids, oracles, () => _collectionService.MoveCardAsync(collectionId, cardId, UserId, request, ct), false, ct);
        return Ok(OfferUndo(change, ids, "move"));
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
        var oracles = await _undo.OraclesAsync(collectionId, UserId, request.CardIds, ct);
        var ids = new[] { collectionId, request.TargetCollectionId };
        var change = await _undo.RunAsync(UserId, ids, oracles, () => _collectionService.MoveCardsAsync(collectionId, UserId, request, ct), false, ct);
        return Ok(OfferUndo(change, ids, "move"));
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
        var result = await _collectionService.MergeCollectionsAsync(collectionId, UserId, request, ct);
        return Ok(result with { Target = result.Target with { Cards = [] } });
    }
}
