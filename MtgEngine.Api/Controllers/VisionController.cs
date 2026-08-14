using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public sealed class VisionController : ControllerBase
{
    /// <summary>Media types the vision model accepts; anything else is rejected here.</summary>
    private static readonly HashSet<string> AllowedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp",
    };

    /// <summary>
    /// Base64 length cap ≈ 5 MB decoded. Scanner frames are ~100 KB JPEGs; anything
    /// near this limit is not a camera frame.
    /// </summary>
    private const int MaxImageBase64Length = 7_000_000;

    private readonly CardVisionService _vision;

    public VisionController(CardVisionService vision) => _vision = vision;

    [EnableRateLimiting("ai")]
    [HttpPost("identify-card")]
    public async Task<IActionResult> IdentifyCard([FromBody] IdentifyCardRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ImageBase64))
            return Problem(detail: "ImageBase64 is required", statusCode: StatusCodes.Status400BadRequest);
        if (request.ImageBase64.Length > MaxImageBase64Length)
            return Problem(detail: "Image is too large", statusCode: StatusCodes.Status400BadRequest);
        var mediaType = request.MediaType ?? "image/jpeg";
        if (!AllowedMediaTypes.Contains(mediaType))
            return Problem(detail: "Unsupported media type", statusCode: StatusCodes.Status400BadRequest);

        // Returned as-is: cardName/setName are Scryfall-verified, and setName is null
        // when the printing could not be determined rather than a model guess.
        var result = await _vision.IdentifyCardAsync(request.ImageBase64, mediaType);
        return Ok(result);
    }
}

public record IdentifyCardRequest(string ImageBase64, string? MediaType);
