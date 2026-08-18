using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

/// <summary>
/// The signed-in user's own profile: the fields only they may edit, and the stats only
/// they may see. The public half is <see cref="UsersController"/>.
/// </summary>
[Authorize]
[ApiController]
[Route("api/profile")]
public sealed class ProfileController : ControllerBase
{
    /// <summary>
    /// Headroom over <see cref="AvatarImage.MaxBytes"/> for multipart framing, so an
    /// upload that is legitimately at the limit is not cut off by the transport before
    /// the validator can give the user a readable answer.
    /// </summary>
    private const long UploadSizeLimit = AvatarImage.MaxBytes + (64 * 1024);

    private readonly IProfileService _profiles;

    // TryParse, not Parse: a token whose subject claim is not a Guid is an auth problem
    // (401), not a FormatException 500.
    private Guid? UserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public ProfileController(IProfileService profiles) => _profiles = profiles;

    /// <summary>The caller's own profile, including the stats withheld from the public one.</summary>
    [HttpGet("me")]
    public async Task<ActionResult<MyProfileDto>> GetMe(CancellationToken ct = default)
    {
        if (UserId is not { } userId)
            return Unauthorized();

        return Ok(await _profiles.GetMyProfileAsync(userId, ct));
    }

    /// <summary>Updates the self-authored profile text. Omitted or blank fields are cleared.</summary>
    [HttpPut("me")]
    public async Task<ActionResult<MyProfileDto>> UpdateMe(
        [FromBody] UpdateProfileRequest request, CancellationToken ct = default)
    {
        if (UserId is not { } userId)
            return Unauthorized();

        return Ok(await _profiles.UpdateMyProfileAsync(userId, request, ct));
    }

    /// <summary>The rules an upload must satisfy, so the client can enforce them before sending.</summary>
    [HttpGet("me/avatar/limits")]
    public ActionResult<AvatarLimitsDto> GetAvatarLimits() =>
        Ok(new AvatarLimitsDto
        {
            MaxBytes = AvatarImage.MaxBytes,
            MaxDimension = AvatarImage.MaxDimension,
            AcceptedContentTypes = AvatarImage.AcceptedContentTypes,
        });

    /// <summary>Replaces the caller's avatar.</summary>
    /// <remarks>
    /// The uploaded part's own content type and filename are ignored — <see cref="AvatarImage"/>
    /// reads the format out of the bytes, and that is what gets stored and later served.
    /// </remarks>
    [HttpPut("me/avatar")]
    [RequestSizeLimit(UploadSizeLimit)]
    public async Task<ActionResult<MyProfileDto>> UploadAvatar(
        IFormFile? file, CancellationToken ct = default)
    {
        if (UserId is not { } userId)
            return Unauthorized();

        if (file is null || file.Length == 0)
            throw new InvalidRequestException("Choose an image to upload.");

        // Checked before reading so an oversized upload is refused without buffering it.
        if (file.Length > AvatarImage.MaxBytes)
            throw new InvalidRequestException($"The image must be {AvatarImage.MaxBytes / 1024} KB or smaller.");

        using var buffer = new MemoryStream((int)file.Length);
        await using (var stream = file.OpenReadStream())
        {
            await stream.CopyToAsync(buffer, ct);
        }

        return Ok(await _profiles.SetAvatarAsync(userId, buffer.ToArray(), ct));
    }

    /// <summary>Removes the caller's avatar, falling the profile back to initials.</summary>
    [HttpDelete("me/avatar")]
    public async Task<ActionResult<MyProfileDto>> DeleteAvatar(CancellationToken ct = default)
    {
        if (UserId is not { } userId)
            return Unauthorized();

        return Ok(await _profiles.DeleteAvatarAsync(userId, ct));
    }
}
