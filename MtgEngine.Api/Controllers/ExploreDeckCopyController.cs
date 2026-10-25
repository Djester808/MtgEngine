using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services.Community;

namespace MtgEngine.Api.Controllers;

public sealed record CopyAverageDeckRequest([StringLength(100)] string Variant = "");

[ApiController, Authorize, Route("api/explore/commanders/{oracleId}/average/copy")]
public sealed class ExploreDeckCopyController(ExploreDeckCopyService service) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<DeckDetailDto>> Copy(string oracleId, CopyAverageDeckRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
            return Unauthorized();
        var deck = await service.Copy(userId, oracleId, request.Variant, ct);
        return Created($"/api/decks/{deck.Id}", deck);
    }
}
