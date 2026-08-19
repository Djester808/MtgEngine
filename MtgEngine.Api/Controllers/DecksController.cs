using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public sealed class DecksController : ControllerBase
{
    private readonly ICollectionService _service;

    private string UserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("User ID claim missing from token");

    public DecksController(ICollectionService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<DeckDto[]>> GetDecks()
    {
        var decks = await _service.GetUserDecksAsync(UserId);
        return Ok(decks);
    }

    [HttpGet("{deckId:guid}")]
    public async Task<ActionResult<DeckDetailDto>> GetDeck(Guid deckId)
    {
        var deck = await _service.GetDeckAsync(deckId, UserId);
        if (deck == null)
            return NotFound();
        return Ok(deck);
    }

    [HttpPost]
    public async Task<ActionResult<DeckDetailDto>> CreateDeck([FromBody] CreateDeckRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Problem(detail: "Deck name is required", statusCode: StatusCodes.Status400BadRequest);

        var deck = await _service.CreateDeckAsync(UserId, request);
        return CreatedAtAction(nameof(GetDeck), new { deckId = deck.Id }, deck);
    }

    [HttpPut("{deckId:guid}")]
    public async Task<ActionResult<DeckDetailDto>> UpdateDeck(Guid deckId, [FromBody] UpdateDeckRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Problem(detail: "Deck name is required", statusCode: StatusCodes.Status400BadRequest);

        try
        {
            var deck = await _service.UpdateDeckAsync(deckId, UserId, request);
            return Ok(deck);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("import")]
    public async Task<ActionResult<ImportDeckResult>> ImportDeck(
        [FromBody] ImportDeckRequest request,
        [FromServices] DeckImportService importer)
    {
        if (string.IsNullOrWhiteSpace(request.Text) && string.IsNullOrWhiteSpace(request.Url))
            return Problem(detail: "Either 'text' or 'url' is required.", statusCode: StatusCodes.Status400BadRequest);
        try
        {
            var result = await importer.ImportAsync(UserId, request);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (HttpRequestException ex)
        {
            return Problem(detail: $"Failed to fetch deck: {ex.Message}", statusCode: StatusCodes.Status400BadRequest);
        }
    }

    [HttpDelete("{deckId:guid}")]
    public async Task<ActionResult> DeleteDeck(Guid deckId)
    {
        var success = await _service.DeleteDeckAsync(deckId, UserId);
        if (!success)
            return NotFound();
        return NoContent();
    }

    // ---- Card management (reuses collection card endpoints) ----

    [HttpPost("{deckId:guid}/cards")]
    public async Task<ActionResult<CollectionCardDto>> AddCard(
        Guid deckId,
        [FromBody] AddCardToCollectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OracleId))
            return Problem(detail: "OracleId is required", statusCode: StatusCodes.Status400BadRequest);

        if (request.Quantity < 0 || request.QuantityFoil < 0 || request.Quantity + request.QuantityFoil < 1)
            return Problem(detail: "Total quantity must be at least 1", statusCode: StatusCodes.Status400BadRequest);

        try
        {
            var (card, created) = await _service.AddCardToCollectionAsync(deckId, UserId, request);
            // 201 only for a genuinely new row; incrementing an existing one is a 200.
            return created ? StatusCode(StatusCodes.Status201Created, card) : Ok(card);
        }
        catch (KeyNotFoundException)
        {
            return Problem(detail: "Deck not found", statusCode: StatusCodes.Status404NotFound);
        }
    }

    [HttpPut("{deckId:guid}/cards/{cardId:guid}")]
    public async Task<ActionResult<CollectionCardDto>> UpdateCard(
        Guid deckId,
        Guid cardId,
        [FromBody] UpdateCollectionCardRequest request)
    {
        if (request.Quantity < 0 || request.QuantityFoil < 0 || request.Quantity + request.QuantityFoil < 1)
            return Problem(detail: "Total quantity must be at least 1", statusCode: StatusCodes.Status400BadRequest);

        try
        {
            var card = await _service.UpdateCollectionCardAsync(deckId, cardId, UserId, request);
            return Ok(card);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpDelete("{deckId:guid}/cards/{cardId:guid}")]
    public async Task<ActionResult> RemoveCard(Guid deckId, Guid cardId)
    {
        var success = await _service.RemoveCardFromCollectionAsync(deckId, cardId, UserId);
        if (!success)
            return NotFound();
        return NoContent();
    }

    // ---- Deck suggestions ------------------------------------------

    [EnableRateLimiting("ai")]
    [HttpPost("suggestions")]
    public async Task<ActionResult<DeckSuggestionsDto>> GetSuggestions(
        [FromBody] DeckSuggestionsRequest request,
        [FromServices] IDeckSuggestionsService suggestionsService)
    {
        if (string.IsNullOrWhiteSpace(request.CommanderOracleId))
            return Problem(detail: "CommanderOracleId is required", statusCode: StatusCodes.Status400BadRequest);

        var result = await suggestionsService.GetSuggestionsAsync(request);
        return Ok(result);
    }

    /// <summary>Web defaults (camelCase) plus enum-as-string, matching the MVC-serialized
    /// non-streaming endpoint so the client parses both responses identically.</summary>
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Server-Sent-Events variant of <see cref="GetSuggestions"/>. Emits coarse "stage" events
    /// and a provisional "cards" event while building, then a single "final" event with the
    /// validated result (or an "error" event). On a cache hit only "final" is sent.
    /// </summary>
    [EnableRateLimiting("ai")]
    [HttpPost("suggestions/stream")]
    public async Task GetSuggestionsStream(
        [FromBody] DeckSuggestionsRequest request,
        [FromServices] IDeckSuggestionsService suggestionsService,
        CancellationToken ct)
    {
        // Validation failures are ordinary HTTP errors — only a valid request becomes
        // an event stream. (A 200 + SSE "error" event hid the failure from anything
        // that reads status codes.)
        if (string.IsNullOrWhiteSpace(request.CommanderOracleId))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(
                new ProblemDetails
                {
                    Detail = "CommanderOracleId is required",
                    Status = StatusCodes.Status400BadRequest,
                },
                ct);
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no"; // stop proxies buffering the stream

        async Task Emit(string @event, object payload)
        {
            var json = JsonSerializer.Serialize(payload, SseJson);
            await Response.WriteAsync($"event: {@event}\ndata: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        try
        {
            var result = await suggestionsService.GetSuggestionsStreamAsync(request, Emit, ct);
            await Emit("final", result);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected mid-stream; nothing to report.
        }
        catch (Exception)
        {
            try
            { await Emit("error", new { message = "Failed to generate suggestions." }); }
            catch { /* client already gone */ }
        }
    }

    // ---- Mana fine-tune -------------------------------------------

    [EnableRateLimiting("ai")]
    [HttpPost("mana-tune")]
    public async Task<ActionResult<ManaFineTuneDto>> GetManaFineTune(
        [FromBody] ManaFineTuneRequest request,
        [FromServices] IManaFineTuneService manaFineTuneService)
    {
        var result = await manaFineTuneService.GetFineTuneAsync(request);
        return Ok(result);
    }

    // ---- AI deck build ---------------------------------------------

    [EnableRateLimiting("ai")]
    [HttpPost("{deckId:guid}/ai-build")]
    public async Task<ActionResult<AiBuildResultDto>> AiBuildDeck(
        Guid deckId,
        [FromBody] AiBuildRequest request,
        [FromServices] IAiBuildService aiBuildService)
    {
        if (string.IsNullOrWhiteSpace(request.CommanderOracleId))
            return Problem(detail: "CommanderOracleId is required", statusCode: StatusCodes.Status400BadRequest);

        var result = await aiBuildService.BuildDeckAsync(deckId, UserId, request);
        return Ok(result);
    }

    /// <summary>
    /// Computes the deck a build would produce, without writing it.
    /// </summary>
    /// <remarks>
    /// The preview half of the build. Same pipeline, stopped one step before the insert, so
    /// the player can see all 99 cards — and what was rejected — before committing a write
    /// they would otherwise have to undo by hand.
    /// </remarks>
    [EnableRateLimiting("ai")]
    [HttpPost("{deckId:guid}/ai-build/plan")]
    public async Task<ActionResult<AiBuildPlanDto>> PlanBuild(
        Guid deckId,
        [FromBody] AiBuildRequest request,
        [FromServices] IAiBuildService aiBuildService)
    {
        if (string.IsNullOrWhiteSpace(request.CommanderOracleId))
            return Problem(detail: "CommanderOracleId is required", statusCode: StatusCodes.Status400BadRequest);

        var plan = await aiBuildService.PlanDeckAsync(deckId, UserId, request);
        return Ok(plan);
    }

    /// <summary>
    /// The same plan, streamed, so a build that takes minutes reports what it is doing.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>suggestions/stream</c>: validation failures are ordinary HTTP errors, and
    /// only a valid request becomes an event stream. Events are <c>stage</c>, then
    /// <c>plan</c> (the deck, before it has been judged), then <c>final</c>.
    /// </remarks>
    [EnableRateLimiting("ai")]
    [HttpPost("{deckId:guid}/ai-build/plan/stream")]
    public async Task PlanBuildStream(
        Guid deckId,
        [FromBody] AiBuildRequest request,
        [FromServices] IAiBuildService aiBuildService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CommanderOracleId))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(
                new ProblemDetails
                {
                    Detail = "CommanderOracleId is required",
                    Status = StatusCodes.Status400BadRequest,
                },
                ct);
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        async Task Emit(string @event, object payload)
        {
            var json = JsonSerializer.Serialize(payload, SseJson);
            await Response.WriteAsync($"event: {@event}\ndata: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        try
        {
            var plan = await aiBuildService.PlanDeckStreamAsync(deckId, UserId, request, Emit, ct);
            await Emit("final", plan);
        }
        catch (OperationCanceledException)
        {
            // Client went away mid-build; nothing to report.
        }
        catch (Exception)
        {
            try
            { await Emit("error", new { message = "Failed to build the deck." }); }
            catch { /* client already gone */ }
        }
    }

    /// <summary>Writes a plan the player accepted. Every card is re-validated on the way in.</summary>
    [HttpPost("{deckId:guid}/ai-build/apply")]
    public async Task<ActionResult<AiBuildResultDto>> ApplyBuildPlan(
        Guid deckId,
        [FromBody] AiApplyPlanRequest request,
        [FromServices] IAiBuildService aiBuildService)
    {
        if (string.IsNullOrWhiteSpace(request.CommanderOracleId))
            return Problem(detail: "CommanderOracleId is required", statusCode: StatusCodes.Status400BadRequest);
        if (request.Cards.Length == 0)
            return Problem(detail: "The plan contains no cards", statusCode: StatusCodes.Status400BadRequest);

        var result = await aiBuildService.ApplyPlanAsync(deckId, UserId, request);
        return Ok(result);
    }

    /// <summary>
    /// Proposes commanders for a deck the player has described but not started.
    /// </summary>
    /// <remarks>
    /// Deliberately not under a deck id: this runs before there is a deck to speak of, and
    /// its answer feeds the plan endpoint above.
    /// </remarks>
    [EnableRateLimiting("ai")]
    [HttpPost("commander-suggestions")]
    public async Task<ActionResult<CommanderSuggestionsDto>> SuggestCommanders(
        [FromBody] CommanderSuggestionRequest request,
        [FromServices] ICommanderSuggestionService suggestions,
        CancellationToken ct)
    {
        var result = await suggestions.SuggestAsync(UserId, request, ct);
        return Ok(result);
    }

    /// <summary>Scores every card in a built deck against its commander, in one call.</summary>
    [EnableRateLimiting("ai")]
    [HttpPost("{deckId:guid}/ai-score")]
    public async Task<ActionResult<DeckScoreDto>> ScoreDeck(
        Guid deckId,
        [FromServices] ISynergyService synergyService)
    {
        var result = await synergyService.ScoreDeckAsync(deckId, UserId);
        return Ok(result);
    }

    /// <summary>Swaps a built deck's weakest cards for better picks from the legal pool.</summary>
    [EnableRateLimiting("ai")]
    [HttpPost("{deckId:guid}/ai-refine")]
    public async Task<ActionResult<AiRefineResultDto>> RefineDeck(
        Guid deckId,
        [FromBody] AiRefineRequest? request,
        [FromServices] IAiBuildService aiBuildService)
    {
        var result = await aiBuildService.RefineDeckAsync(deckId, UserId, request ?? new AiRefineRequest());
        return Ok(result);
    }

    /// <summary>
    /// Applies swaps the player accepted from a refine preview.
    /// </summary>
    /// <remarks>
    /// Not rate-limited as an AI route because it asks the model nothing — it is the accept
    /// half of a preview that already ran. Every swap is still validated server-side.
    /// </remarks>
    [HttpPost("{deckId:guid}/ai-refine/apply")]
    public async Task<ActionResult<AiRefineResultDto>> ApplyRefineSwaps(
        Guid deckId,
        [FromBody] AiApplySwapsRequest request,
        [FromServices] IAiBuildService aiBuildService)
    {
        var result = await aiBuildService.ApplySwapsAsync(deckId, UserId, request);
        return Ok(result);
    }

    // ---- Synergy scoring -------------------------------------------

    [EnableRateLimiting("ai")]
    [HttpPost("synergy")]
    public async Task<ActionResult<SynergyResultDto>> GetSynergy(
        [FromBody] SynergyRequest request,
        [FromServices] ISynergyService synergyService)
    {
        if (string.IsNullOrWhiteSpace(request.CommanderOracleId) || string.IsNullOrWhiteSpace(request.CardOracleId))
            return Problem(detail: "CommanderOracleId and CardOracleId are required", statusCode: StatusCodes.Status400BadRequest);

        var result = await synergyService.GetSynergyAsync(request);
        return Ok(result);
    }

    /// <summary>
    /// Scores a page of cards against a commander in one call. Used by the browse list,
    /// which would otherwise fire one request per row.
    /// </summary>
    [EnableRateLimiting("ai")]
    [HttpPost("synergy/batch")]
    public async Task<ActionResult<ScoredCardDto[]>> GetSynergyBatch(
        [FromBody] SynergyBatchRequest request,
        [FromServices] ISynergyService synergyService)
    {
        if (string.IsNullOrWhiteSpace(request.CommanderOracleId))
            return Problem(detail: "CommanderOracleId is required", statusCode: StatusCodes.Status400BadRequest);
        if (request.CardOracleIds.Length == 0)
            return Ok(Array.Empty<ScoredCardDto>());
        if (request.CardOracleIds.Length > MaxBatchScoreCards)
            return BadRequest($"At most {MaxBatchScoreCards} cards can be scored per request.");

        // Deck-aware needs a deck; this endpoint scores a loose set of cards, so an
        // explicit "deck-aware" without one falls back to ideal inside the service.
        var mode = string.Equals(request.Mode, "deck-aware", StringComparison.OrdinalIgnoreCase)
            ? ScoringMode.DeckAware
            : ScoringMode.Ideal;

        var result = await synergyService.ScoreCardsAsync(
            request.CommanderOracleId, request.CardOracleIds, mode, profile: null, focus: request.Focus);

        return Ok(result);
    }

    /// <summary>One browse page. Larger batches risk truncating the model's reply.</summary>
    private const int MaxBatchScoreCards = 40;
}
