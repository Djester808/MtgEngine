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
public sealed class GamesController(GameReportService reports, GameSessionService sessions, GameArtworkService? artwork = null) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;


    /// <summary>A seated player's redacted snapshot. Account identity selects the seat.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> View(Guid id, CancellationToken ct)
    {
        var session = await sessions.FindAsync(id, ct).ConfigureAwait(false);
        if (session is null || session.SeatOf(UserId) is not { } seat)
            return NotFound();
        var (view, legal) = session.ProjectFor(seat);
        var images = artwork is null ? null : await artwork.ForAsync(view, ct).ConfigureAwait(false);
        return Ok(new { GameId = id, View = view, Legal = legal, Artwork = images });
    }

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
