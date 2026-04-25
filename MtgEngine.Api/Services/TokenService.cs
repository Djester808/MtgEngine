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
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Secret"]!));
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
}
