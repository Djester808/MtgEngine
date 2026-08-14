using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;

namespace MtgEngine.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/preferences")]
public sealed class PreferencesController : ControllerBase
{
    private readonly MtgEngineDbContext _db;

    // TryParse, not Parse: a token whose subject claim is not a Guid is an auth
    // problem (401), not a FormatException 500.
    private Guid? UserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public PreferencesController(MtgEngineDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<UserPreferencesDto>> GetPreferences()
    {
        if (UserId is not { } userId)
            return Unauthorized();
        var user = await _db.Users.FindAsync(userId);
        if (user == null)
            return NotFound();

        UserPreferencesDto prefs;
        try
        {
            prefs = string.IsNullOrEmpty(user.PreferencesJson)
                ? new UserPreferencesDto()
                : JsonSerializer.Deserialize<UserPreferencesDto>(user.PreferencesJson)
                  ?? new UserPreferencesDto();
        }
        catch (JsonException)
        {
            // A corrupt stored blob resets to defaults rather than 500ing forever.
            prefs = new UserPreferencesDto();
        }

        return Ok(prefs);
    }

    [HttpPut]
    public async Task<ActionResult<UserPreferencesDto>> UpdatePreferences([FromBody] UserPreferencesDto request)
    {
        if (UserId is not { } userId)
            return Unauthorized();
        var user = await _db.Users.FindAsync(userId);
        if (user == null)
            return NotFound();

        user.PreferencesJson = JsonSerializer.Serialize(request);
        await _db.SaveChangesAsync();
        return Ok(request);
    }
}
