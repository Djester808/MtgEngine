using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Rules.Views;

namespace MtgEngine.Api.Controllers;

/// <summary>Starting and finding games. Playing one happens over the hub.</summary>
[ApiController]
[Route("api/games")]
public sealed class GamesController : ControllerBase
{
    private readonly GameSessionService _sessions;
    private readonly GameTableService _tables;

    public GamesController(GameSessionService sessions, GameTableService tables)
    {
        _sessions = sessions;
        _tables = tables;
    }

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    /// <summary>Starts a game between the given decks.</summary>
    [HttpPost]
    public async Task<ActionResult<GameStartedDto>> Create(
        [FromBody] CreateGameRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var gameId = await _tables.StartAsync(CurrentUserId, request, ct).ConfigureAwait(false);
        return Ok(new GameStartedDto(gameId));
    }

    /// <summary>
    /// The caller's view of a game (CR 400.2), for a client that has just loaded the page.
    /// </summary>
    /// <remarks>
    /// The same projection the hub pushes. It is here so a refresh does not have to wait for the
    /// next action to see the board — not as a second way to read state, which is why it returns
    /// a <see cref="GameView"/> and there is no endpoint that returns anything else.
    /// </remarks>
    [HttpGet("{gameId:guid}")]
    public async Task<ActionResult<GameView>> Get(Guid gameId, CancellationToken ct)
    {
        var session = _sessions.Find(gameId);
        if (session is null)
            return NotFound();

        if (!session.SeatNames.ContainsKey(CurrentUserId))
            return Forbid();

        return Ok(await session.ReadAsync(CurrentUserId, ct).ConfigureAwait(false));
    }

    /// <summary>The game's log, as lines a player can read.</summary>
    [HttpGet("{gameId:guid}/log")]
    public async Task<ActionResult<IReadOnlyList<string>>> Log(Guid gameId, CancellationToken ct)
    {
        var session = _sessions.Find(gameId);
        if (session is null)
            return NotFound();

        if (!session.SeatNames.ContainsKey(CurrentUserId))
            return Forbid();

        return Ok(await session.LogAsync(ct).ConfigureAwait(false));
    }
}
