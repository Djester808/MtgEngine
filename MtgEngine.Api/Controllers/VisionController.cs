using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public sealed class VisionController : ControllerBase
{
    private readonly CardVisionService _vision;

    public VisionController(CardVisionService vision) => _vision = vision;

    [HttpPost("identify-card")]
    public async Task<IActionResult> IdentifyCard([FromBody] IdentifyCardRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ImageBase64))
            return BadRequest("ImageBase64 is required");

        // Returned as-is: cardName/setName are Scryfall-verified, and setName is null
        // when the printing could not be determined rather than a model guess.
        var result = await _vision.IdentifyCardAsync(
            request.ImageBase64,
            request.MediaType ?? "image/jpeg"
        );
        return Ok(result);
    }
}

public record IdentifyCardRequest(string ImageBase64, string? MediaType);
