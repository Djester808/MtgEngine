using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Cards;

namespace MtgEngine.Api.Controllers;

/// <summary>
/// How far along the card engine is: one card, the whole corpus, and settling a dispute. Thin —
/// bind, one service call, return; the typed exceptions the service throws become ProblemDetails
/// in <c>AiExceptionHandler</c>.
/// </summary>
[ApiController]
[Route("api/cards")]
[Authorize]
public sealed class CardsImplementationController(CardImplementationService implementation) : ControllerBase
{
    /// <summary>How far along one card is, with the scenarios and the last verification behind it.</summary>
    [HttpGet("{oracleId}/implementation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<CardImplementationDto> Implementation(string oracleId) =>
        implementation.Implementation(oracleId);

    /// <summary>How far along each of a deck's cards is, in the order they were asked about.</summary>
    [HttpPost("implementations")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<IReadOnlyList<CardImplementationDto>> Implementations([FromBody] CardImplementationsRequest request) =>
        Ok(implementation.Implementations(request.OracleIds));

    /// <summary>Counts by status, the primitive backlog ranked by cards blocked, and the last run.</summary>
    [HttpGet("coverage")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<CardCoverageDto> Coverage([FromQuery] ImplementationStatus? status) =>
        implementation.Coverage(status);

    /// <summary>Settles a disputed card on one model's reading.</summary>
    [HttpPost("{oracleId}/implementation/choose")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<CardImplementationDto> Choose(string oracleId, [FromBody] ChooseReadingRequest request) =>
        implementation.Choose(oracleId, request.Model);
}
