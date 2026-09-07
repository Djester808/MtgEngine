using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

/// <summary>
/// What a game needs over HTTP. Play itself is the hub; this is what outlives a connection.
/// </summary>
[ApiController]
[Route("api/games")]
[Authorize]
public sealed class GamesController(GameReportService reports) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    /// <summary>Files a report that a card did not play as printed, from a position in the log.</summary>
    [HttpPost("{id:guid}/reports")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GameReportDto>> Report(Guid id, [FromBody] GameReportRequest request, CancellationToken ct)
    {
        var filed = await reports.FileAsync(id, UserId, request, ct).ConfigureAwait(false);
        return Created($"/api/games/{id}/reports/{filed.Id}", filed);
    }
}
