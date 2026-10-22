using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

[ApiController, Authorize, Route("api/collections")]
public sealed class CollectionMetadataController(CollectionMetadataService metadata, CollectionMutationUndoService mutations) : ControllerBase
{
    private string Me => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    [HttpGet("{collectionId:guid}/cards/{rowId:guid}/metadata")]
    public Task<CollectionMetadataRequest> Get(Guid collectionId, Guid rowId, CancellationToken ct, [FromQuery] string? finish = null) => metadata.GetAsync(collectionId, rowId, Me, ct, finish);
    [HttpPut("{collectionId:guid}/cards/{rowId:guid}/metadata")]
    public async Task<IActionResult> Update(Guid collectionId, Guid rowId, CollectionMetadataRequest request, CancellationToken ct)
    {
        await metadata.UpdateAsync(collectionId, rowId, Me, request, ct).ConfigureAwait(false);
        return NoContent();
    }
    [HttpPost("{collectionId:guid}/cards/bulk")]
    public Task<CollectionBulkResponse> Bulk(Guid collectionId, CollectionBulkRequest request, CancellationToken ct) => metadata.BulkAsync(collectionId, Me, request, ct);
    [HttpPost("undo/{token:guid}")]
    public async Task<IActionResult> Undo(Guid token, CancellationToken ct)
    {
        if (!await mutations.TryUndoAsync(token, Me, ct).ConfigureAwait(false))
            await metadata.UndoAsync(token, Me, ct).ConfigureAwait(false);
        return NoContent();
    }
}
