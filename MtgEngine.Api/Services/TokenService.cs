using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MtgEngine.Api.Data;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public sealed class TokenService
{
    private readonly SymmetricSecurityKey _key;

    private readonly MtgEngineDbContext? _db;

    public TokenService(IConfiguration config, MtgEngineDbContext? db = null)
    {
        _db = db;
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecretConfig.JwtSecret(config)));
    }

    public string Generate(User user, Guid? sessionFamily = null)
    {
        var handler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim("ver", user.TokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim("session", sessionFamily?.ToString() ?? "legacy"),
            ]),
            Expires = sessionFamily.HasValue ? DateTime.UtcNow.AddMinutes(15) : DateTime.UtcNow.AddDays(30),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        };
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    /// <summary>
    /// Reads the user id out of a token that is not the caller's own.
    /// </summary>
    /// <remarks>
    /// The life counter runs on one device with several people sitting around it, so a request
    /// can carry a token per seat and the <c>Authorization</c> header can only speak for one of
    /// them. This validates each of the others exactly as the middleware validates that one —
    /// same key, same algorithm, same lifetime — because a seat's token is the only evidence
    /// that the person in that seat agreed to have the game counted against their record.
    /// <para>
    /// Returns false for anything it cannot vouch for: wrong signature, expired, a token signed
    /// with <c>alg: none</c>, or malformed. It never throws — a bad token is a normal answer to
    /// this question, not an exception.
    /// </para>
    /// </remarks>
    public bool TryReadUserId(string? token, out Guid userId)
    {
        userId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _key,
            // Generate() writes neither, and the middleware does not require them; asking for
            // them here would reject every token this service itself produces.
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            // Pinning the algorithm is what stops a token that declares "none" or a weaker
            // family from being taken on its own word.
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        };

        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out userId))
                return false;
            if (_db is null)
                return true;
            var id = userId;
            var version = principal.FindFirstValue("ver") ?? "0";
            var user = _db.Users.AsNoTracking().FirstOrDefault(u => u.Id == id);
            if (user is null || version != user.TokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
                return false;
            var family = principal.FindFirstValue("session");
            return family is null or "legacy" || (Guid.TryParse(family, out var session) && _db.AccountSessions.Any(s =>
                s.UserId == id && s.FamilyId == session && s.RevokedAt == null && s.ExpiresAt > DateTime.UtcNow));
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            return false;
        }
    }
}
