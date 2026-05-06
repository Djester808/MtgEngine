using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Mapping;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class GamesController : ControllerBase
{
    private readonly GameSessionService _sessions;
    private readonly IScryfallService _scryfall;

    public GamesController(GameSessionService sessions, IScryfallService scryfall)
    {
        _sessions = sessions;
        _scryfall = scryfall;
    }

    // POST /api/games
    [HttpPost]
    public async Task<ActionResult<CreateGameResponse>> CreateGame(
        [FromBody] CreateGameRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Player1Name) ||
            string.IsNullOrWhiteSpace(request.Player2Name))
            return BadRequest("Player names are required.");

        var session = await _sessions.CreateAsync(
            request.Player1Name,
            request.Player2Name,
            request.Player1DeckList,
            request.Player2DeckList);

        return Ok(new CreateGameResponse(
            session.GameId.ToString(),
            session.Player1Token,
            session.Player2Token));
    }

    // GET /api/games/{gameId}
    [HttpGet("{gameId:guid}")]
    public ActionResult<GameStateDto> GetGame(
        Guid gameId,
        [FromHeader(Name = "X-Player-Token")] string? token)
    {
        var session = _sessions.Get(gameId);
        if (session is null) return NotFound();

        var playerId = ResolvePlayer(session, token);
        return Ok(DomainMapper.ToDto(session.State, playerId));
    }

    // POST /api/games/{gameId}/join
    [HttpPost("{gameId:guid}/join")]
    public ActionResult<JoinGameResponse> JoinGame(
        Guid gameId,
        [FromBody] JoinGameRequest request)
    {
        var session = _sessions.Get(gameId);
        if (session is null) return NotFound();

        if (!session.TryResolveToken(request.PlayerToken, out var playerId))
            return Unauthorized("Invalid player token.");

        var state = DomainMapper.ToDto(session.State, playerId);
        return Ok(new JoinGameResponse(
            gameId.ToString(),
            request.PlayerToken,
            playerId.ToString(),
            state));
    }

    // ---- Helpers ------------------------------------------

    private static Guid ResolvePlayer(GameSession session, string? token)
    {
        if (token is not null && session.TryResolveToken(token, out var id)) return id;
        return session.Player1Id; // default to player 1 for anonymous GET
    }
}

// ---- Cards controller (Scryfall proxy) --------------------

[ApiController]
[Route("api/[controller]")]
public sealed class CardsController : ControllerBase
{
    private readonly IScryfallService _scryfall;

    public CardsController(IScryfallService scryfall)
    {
        _scryfall = scryfall;
    }

    // GET /api/cards/{oracleId}
    [HttpGet("{oracleId}")]
    public async Task<ActionResult<CardDto>> GetCard(string oracleId)
    {
        var def = await _scryfall.GetByOracleIdAsync(oracleId);
        if (def is null) return NotFound();

        var card = new Domain.Models.Card
        {
            Definition = def,
            OwnerId    = Guid.Empty,
        };
        return Ok(DomainMapper.ToDto(card));
    }

    // GET /api/cards/named?name=Mountain
    [HttpGet("named")]
    public async Task<ActionResult<CardDto>> GetCardByName([FromQuery] string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return BadRequest();
        var def = await _scryfall.GetByNameAsync(name.Trim());
        if (def is null) return NotFound();
        return Ok(DomainMapper.ToDto(new Domain.Models.Card { Definition = def, OwnerId = Guid.Empty }));
    }

    // GET /api/cards/search?q=...&limit=60&offset=0&sortBy=name&sortDir=asc&matchCase=false&matchWord=false&useRegex=false
    [HttpGet("search")]
    public async Task<ActionResult<CardDto[]>> Search(
        [FromQuery] string q,
        [FromQuery] int    limit     = 60,
        [FromQuery] int    offset    = 0,
        [FromQuery] string sortBy    = "name",
        [FromQuery] string sortDir   = "asc",
        [FromQuery] bool   matchCase = false,
        [FromQuery] bool   matchWord = false,
        [FromQuery] bool   useRegex  = false)
    {
        if (string.IsNullOrWhiteSpace(q)) return BadRequest();

        var defs = await _scryfall.SearchAsync(
            q,
            Math.Clamp(limit, 1, 60),
            Math.Max(offset, 0),
            sortBy,
            sortDir,
            matchCase,
            matchWord,
            useRegex);
        var cards = defs.Select(def => DomainMapper.ToDto(
            new Domain.Models.Card { Definition = def, OwnerId = Guid.Empty })).ToArray();
        return Ok(cards);
    }

    // GET /api/cards/sets?q=...
    [HttpGet("sets")]
    public async Task<ActionResult<SetSummaryDto[]>> GetSets([FromQuery] string? q = null)
    {
        var sets = await _scryfall.GetSetsAsync(q);
        return Ok(sets);
    }

    // GET /api/cards/scryfall/{scryfallId}
    [HttpGet("scryfall/{scryfallId}")]
    public async Task<ActionResult<CardDto>> GetCardByScryfallId(string scryfallId)
    {
        var def = await _scryfall.GetByScryfallIdAsync(scryfallId);
        if (def is null) return NotFound();
        return Ok(DomainMapper.ToDto(def, Guid.Empty, Guid.Empty));
    }

    // GET /api/cards/{oracleId}/printings
    [HttpGet("{oracleId}/printings")]
    public async Task<ActionResult<PrintingDto[]>> GetPrintings(string oracleId)
    {
        var printings = await _scryfall.GetPrintingsAsync(oracleId);
        return Ok(printings);
    }

    // GET /api/cards/{oracleId}/rulings
    [HttpGet("{oracleId}/rulings")]
    public async Task<ActionResult<RulingDto[]>> GetRulings(string oracleId)
    {
        var rulings = await _scryfall.GetRulingsAsync(oracleId);
        return Ok(rulings);
    }
}
