using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Controllers;

[ApiController]
[AllowAnonymous]
[EnableRateLimiting("auth")]
[Route("api/[controller]")]
public sealed class AuthController : ControllerBase
{
    private readonly MtgEngineDbContext _db;
    private readonly TokenService _tokens;

    public AuthController(MtgEngineDbContext db, TokenService tokens)
    {
        _db = db;
        _tokens = tokens;
    }

    [HttpPost("register")]
    public async Task<ActionResult<AuthTokenResponse>> Register([FromBody] RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username)
            || string.IsNullOrWhiteSpace(request.Email)
            || string.IsNullOrWhiteSpace(request.Password))
            return Problem(detail: "Username, email, and password are required", statusCode: StatusCodes.Status400BadRequest);

        if (request.Password.Length < 8)
            return Problem(detail: "Password must be at least 8 characters", statusCode: StatusCodes.Status400BadRequest);

        var taken = await _db.Users.AnyAsync(u =>
            u.Username == request.Username || u.Email == request.Email);
        if (taken)
            return Problem(detail: "Username or email is already taken", statusCode: StatusCodes.Status409Conflict);

        var user = new User
        {
            Username = request.Username,
            Email = request.Email,
            PasswordHash = PasswordHasher.Hash(request.Password),
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return Ok(new AuthTokenResponse(_tokens.Generate(user), user.Username));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthTokenResponse>> Login([FromBody] LoginRequest request)
    {
        // Accept either username or email in the Username field
        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.Username == request.Username || u.Email == request.Username);

        if (user is null)
        {
            // Burn the same hashing cost as a real check so response timing does not
            // reveal whether the account exists.
            PasswordHasher.VerifyDummy(request.Password);
            return Problem(detail: "Invalid credentials", statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!PasswordHasher.Verify(request.Password, user.PasswordHash))
            return Problem(detail: "Invalid credentials", statusCode: StatusCodes.Status401Unauthorized);

        return Ok(new AuthTokenResponse(_tokens.Generate(user), user.Username));
    }
}
