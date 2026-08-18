using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

/// <summary>Public community profiles — read-only, browsable without a login.</summary>
/// <remarks>
/// This controller used to build the whole profile projection inline against the
/// <c>DbContext</c>. That work now lives in <see cref="IProfileService"/>; the owner-only
/// half of the same domain is <see cref="ProfileController"/>.
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("api/users")]
public sealed class UsersController : ControllerBase
{
    private readonly IProfileService _profiles;

    public UsersController(IProfileService profiles) => _profiles = profiles;

    /// <summary>Lists community members, most active first.</summary>
    [HttpGet]
    public async Task<ActionResult<PlayerSummaryDto[]>> GetPlayers(
        [FromQuery] int limit = 100, CancellationToken ct = default) =>
        Ok(await _profiles.GetPlayersAsync(limit, ct));

    /// <summary>Public profile: who they are, their stats, decks and recent comments.</summary>
    [HttpGet("{username}")]
    public async Task<ActionResult<UserProfileDto>> GetProfile(
        string username, CancellationToken ct = default) =>
        Ok(await _profiles.GetPublicProfileAsync(username, ct));

    /// <summary>A page of a user's comment history, newest first.</summary>
    [HttpGet("{username}/comments")]
    public async Task<ActionResult<UserCommentPageDto>> GetComments(
        string username,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = ProfileService.DefaultCommentPageSize,
        CancellationToken ct = default) =>
        Ok(await _profiles.GetCommentHistoryAsync(username, page, pageSize, ct));

    /// <summary>Streams a user's avatar.</summary>
    /// <remarks>
    /// The content type is the one sniffed from the bytes at upload, never the uploader's
    /// claim, and it is pinned with <c>nosniff</c> so no browser re-interprets a stored
    /// blob as script or markup. Caching is immutable-for-a-year because the URL carries a
    /// <c>v</c> stamp that changes whenever the image does — a new avatar is a new URL.
    /// </remarks>
    [HttpGet("{username}/avatar")]
    public async Task<IActionResult> GetAvatar(string username, CancellationToken ct = default)
    {
        var avatar = await _profiles.GetAvatarAsync(username, ct);
        if (avatar is null)
            return NotFound();

        var etag = new EntityTagHeaderValue(avatar.ETag);

        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentDisposition = "inline";
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";

        // File() handles the conditional request: a matching If-None-Match returns 304.
        return File(avatar.Data, avatar.ContentType, avatar.UpdatedAt, etag);
    }
}
