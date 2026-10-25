using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/games/lobbies")]
public sealed class GameLobbiesController(GameLobbyService lobbies, IGameLobbyDecks decks, GameLobbyStartup startup) : ControllerBase
{
    private string Account => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    [HttpGet("players")]
    public async Task<ActionResult<IReadOnlyList<GameLobbyPlayerDto>>> Players([FromQuery] string? search, CancellationToken ct) =>
        Ok(await lobbies.PlayersAsync(Account, search, ct).ConfigureAwait(false));

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GameLobbyDto>>> List(CancellationToken ct) =>
        Ok(await lobbies.ListAsync(Account, ct).ConfigureAwait(false));

    [HttpGet("decks")]
    public async Task<ActionResult<IReadOnlyList<LobbyDeckChoice>>> Decks(CancellationToken ct) =>
        Ok(await decks.ListAsync(Account, ct).ConfigureAwait(false));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<GameLobbyDto>> Get(Guid id, CancellationToken ct) =>
        Ok(await lobbies.GetAsync(id, Account, ct).ConfigureAwait(false));

    [HttpPost]
    public async Task<ActionResult<GameLobbyDto>> Create([FromBody] CreateGameLobbyRequest request, CancellationToken ct)
    {
        var lobby = await lobbies.CreateAsync(Account, request, ct).ConfigureAwait(false);
        return CreatedAtAction(nameof(Get), new { id = lobby.Id }, lobby);
    }

    [HttpPost("{id:guid}/deck")]
    public async Task<ActionResult<GameLobbyDto>> BringDeck(Guid id, [FromBody] BringLobbyDeckRequest request, CancellationToken ct) =>
        Ok(await lobbies.BringDeckAsync(id, Account, request, ct).ConfigureAwait(false));

    [HttpPost("{id:guid}/leave")]
    public async Task<ActionResult<GameLobbyDto>> Leave(Guid id, [FromBody] LobbyRevisionRequest request, CancellationToken ct) =>
        Ok(await lobbies.LeaveAsync(id, Account, request.Revision, ct).ConfigureAwait(false));

    [HttpPost("{id:guid}/start")]
    public async Task<ActionResult<GameLobbyStartDto>> Start(Guid id, [FromBody] LobbyRevisionRequest request, CancellationToken ct) =>
        Ok(await startup.StartAsync(id, Account, request.Revision, ct).ConfigureAwait(false));
}
