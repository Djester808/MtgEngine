using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

[ApiController, Route("api")]
public sealed class PublicCollectionsController(PublicCollectionService service) : ControllerBase
{
    [AllowAnonymous, HttpGet("public-collections/{username}/summary")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<PublicCollectionSummary> Summary(string username, CancellationToken ct) => service.Summary(username, ct);

    [Authorize, HttpGet("profile/me/collection-visibility")]
    public async Task<ActionResult<CollectionVisibilityRequest>> Visibility(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            return Unauthorized();
        return new CollectionVisibilityRequest(await service.Visibility(id, ct));
    }
    [Authorize, HttpPut("profile/me/collection-visibility")]
    public async Task<IActionResult> SetVisibility(CollectionVisibilityRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            return Unauthorized();
        await service.SetVisibility(id, request.Public, ct);
        return NoContent();
    }
}
