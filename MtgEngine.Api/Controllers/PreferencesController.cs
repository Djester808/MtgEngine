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

    private Guid UserId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("User ID claim missing from token"));

    public PreferencesController(MtgEngineDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<UserPreferencesDto>> GetPreferences()
    {
        var user = await _db.Users.FindAsync(UserId);
        if (user == null) return NotFound();

        var prefs = string.IsNullOrEmpty(user.PreferencesJson)
            ? new UserPreferencesDto()
            : JsonSerializer.Deserialize<UserPreferencesDto>(user.PreferencesJson) ?? new UserPreferencesDto();

        return Ok(prefs);
    }

    [HttpPut]
    public async Task<ActionResult<UserPreferencesDto>> UpdatePreferences([FromBody] UserPreferencesDto request)
    {
        var user = await _db.Users.FindAsync(UserId);
        if (user == null) return NotFound();

        user.PreferencesJson = JsonSerializer.Serialize(request);
        await _db.SaveChangesAsync();
        return Ok(request);
    }
}
