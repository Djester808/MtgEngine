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
    private readonly GameInviteService _invites;

    public GamesController(
        GameSessionService sessions, GameTableService tables, GameInviteService invites)
    {
        _sessions = sessions;
        _tables = tables;
        _invites = invites;
    }

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

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

    // ---- Invitations ---------------------------------------------------------------------

    /// <summary>The decks the caller could bring to a game.</summary>
    /// <remarks>
    /// Only the caller's own. A deck list is not public, so there is deliberately no endpoint
    /// that returns somebody else's — each player names their own deck, the inviter when they
    /// invite and the opponent when they accept.
    /// </remarks>
    [HttpGet("decks")]
    public async Task<ActionResult<PlayableDeckDto[]>> Decks(CancellationToken ct) =>
        Ok(await _tables.PlayableDecksAsync(CurrentUserId, ct).ConfigureAwait(false));

    /// <summary>Invites another player to a game.</summary>
    [HttpPost("invites")]
    public ActionResult<GameInviteDto> Invite([FromBody] CreateInviteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var invite = _invites.Create(
            CurrentUserId,
            User.FindFirstValue(ClaimTypes.Name) ?? "A player",
            request.DeckId,
            request.OpponentUserId,
            request.StartingLife);

        return Ok(ToDto(invite));
    }

    /// <summary>Invitations waiting for the caller to answer.</summary>
    [HttpGet("invites")]
    public ActionResult<GameInviteDto[]> Invites() =>
        Ok(_invites.For(CurrentUserId).Select(ToDto).ToArray());

    /// <summary>Invitations the caller has sent and nobody has answered.</summary>
    [HttpGet("invites/sent")]
    public ActionResult<GameInviteDto[]> Sent() =>
        Ok(_invites.SentBy(CurrentUserId).Select(ToDto).ToArray());

    /// <summary>Accepts an invitation, which starts the game.</summary>
    [HttpPost("invites/{inviteId:guid}/accept")]
    public async Task<ActionResult<GameStartedDto>> Accept(
        Guid inviteId, [FromBody] AcceptInviteRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Taken rather than read: two taps race, one wins, and the loser gets a 404 instead of
        // a second game against the same deck.
        var invite = _invites.Take(inviteId, CurrentUserId);
        if (invite is null)
            return NotFound();

        var gameId = await _tables
            .StartAsync(
                invite.FromUserId,
                invite.FromDeckId,
                CurrentUserId,
                request.DeckId,
                invite.StartingLife,
                ct)
            .ConfigureAwait(false);

        return Ok(new GameStartedDto(gameId));
    }

    /// <summary>Withdraws an invitation the caller sent.</summary>
    [HttpDelete("invites/{inviteId:guid}")]
    public IActionResult Withdraw(Guid inviteId) =>
        _invites.Withdraw(inviteId, CurrentUserId) ? NoContent() : NotFound();

    private static GameInviteDto ToDto(GameInvite invite) => new(
        invite.Id, invite.FromUserId, invite.FromUserName, invite.StartingLife, invite.CreatedUtc);

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
