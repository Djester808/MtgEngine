using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

[ApiController]
[Route("api/admin")]
public sealed class AdminController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AdminController> _logger;

    private static int _seedingInProgress = 0;

    public AdminController(IServiceScopeFactory scopeFactory, ILogger<AdminController> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Deletes all seed data created by CommunityBot so you can re-seed cleanly.</summary>
    [HttpDelete("clear-seed")]
    public async Task<IActionResult> ClearSeed([FromServices] CommanderDeckSeeder seeder)
    {
        if (_seedingInProgress == 1)
            return Conflict(new { message = "Seeding in progress — wait for it to finish first." });
        var result = await seeder.ClearSeedAsync();
        return Ok(new { message = result });
    }

    /// <summary>
    /// Seeds the database with community commander decks pulled from EDHREC + Scryfall.
    /// Runs in the background — poll logs or re-check commander list to confirm completion.
    /// </summary>
    [HttpPost("seed-commanders")]
    public IActionResult SeedCommanders(
        [FromQuery] int commanders = 50,
        [FromQuery] int decksEach = 10)
    {
        if (Interlocked.CompareExchange(ref _seedingInProgress, 1, 0) != 0)
            return Conflict(new { message = "Seeding already in progress." });

        commanders = Math.Clamp(commanders, 1, 200);
        decksEach = Math.Clamp(decksEach, 1, 20);

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var seeder = scope.ServiceProvider.GetRequiredService<CommanderDeckSeeder>();
                var result = await seeder.SeedAsync(commanders, decksEach);
                _logger.LogInformation("Seed complete: {Result}", result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Seed task failed");
            }
            finally
            {
                Interlocked.Exchange(ref _seedingInProgress, 0);
            }
        });

        return Accepted(new
        {
            message = $"Seeding {commanders} commanders × {decksEach} decks started in background.",
            hint = "Check API logs or GET /api/commanders for progress.",
        });
    }
}
