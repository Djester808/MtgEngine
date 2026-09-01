using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

/// <summary>
/// Games played on the life counter, and what they add up to.
/// </summary>
/// <remarks>
/// Not the same thing as <c>GamesController</c>: that one runs games the engine plays, and its
/// record of a game is the event log. These are games played with real cards on a real table,
/// where the only witness is the counter someone had open next to them.
/// </remarks>
[Authorize]
[ApiController]
[Route("api/matches")]
public sealed class MatchesController : ControllerBase
{
    private readonly ILifeMatchService _matches;

    public MatchesController(ILifeMatchService matches)
    {
        _matches = matches;
    }

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    /// <summary>Files a finished game.</summary>
    /// <remarks>
    /// Requires a signed-in caller so a stream of these has an owner, but the caller need not be
    /// seated — one person holding the phone for the table is the normal case. Which seats
    /// actually count towards an account is decided by the tokens inside the body, never by the
    /// caller's own.
    /// </remarks>
    [HttpPost]
    public async Task<ActionResult<MatchRecordedDto>> Record(
        [FromBody] RecordMatchRequest request,
        CancellationToken ct) =>
        Ok(await _matches.RecordAsync(CurrentUserId, request, ct).ConfigureAwait(false));

    /// <summary>The caller's win/loss record and their last few games.</summary>
    [HttpGet("me")]
    public async Task<ActionResult<PlayerRecordDto>> Mine(CancellationToken ct) =>
        Ok(await _matches.GetRecordAsync(CurrentUserId, ct).ConfigureAwait(false));
}
