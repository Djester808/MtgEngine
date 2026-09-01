using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public sealed class TokenService
{
    private readonly SymmetricSecurityKey _key;

    public TokenService(IConfiguration config)
    {
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecretConfig.JwtSecret(config)));
    }

    public string Generate(User user)
    {
        var handler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
            ]),
            Expires = DateTime.UtcNow.AddDays(30),
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
            return Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            return false;
        }
    }
}
