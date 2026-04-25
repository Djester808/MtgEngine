using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;
using Xunit;

namespace MtgEngine.Rules.Tests;

public sealed class TokenServiceTests
{
    private static readonly string TestSecret = "test-jwt-secret-key-32-chars-min!!";

    private static TokenService MakeService() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection([new("Jwt:Secret", TestSecret)])
            .Build());

    private static User MakeUser(string username = "testuser") => new()
    {
        Id       = Guid.NewGuid(),
        Username = username,
        Email    = $"{username}@example.com",
    };

    // ---- Shape ----

    [Fact]
    public void Generate_ReturnsNonEmptyString()
    {
        MakeService().Generate(MakeUser()).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Generate_ReturnsValidJwtFormat()
    {
        var token = MakeService().Generate(MakeUser());
        // JWTs have exactly three base64url segments separated by '.'
        token.Split('.').Should().HaveCount(3);
    }

    // ---- Claims ----

    [Fact]
    public void Generate_ContainsNameIdentifierEqualToUserId()
    {
        var user  = MakeUser();
        var token = MakeService().Generate(user);
        var jwt   = new JwtSecurityTokenHandler().ReadJwtToken(token);

        jwt.Claims
            .First(c => c.Type == ClaimTypes.NameIdentifier || c.Type == "nameid")
            .Value.Should().Be(user.Id.ToString());
    }

    [Fact]
    public void Generate_ContainsNameEqualToUsername()
    {
        var user  = MakeUser("alice");
        var token = MakeService().Generate(user);
        var jwt   = new JwtSecurityTokenHandler().ReadJwtToken(token);

        jwt.Claims
            .First(c => c.Type == ClaimTypes.Name || c.Type == "unique_name")
            .Value.Should().Be("alice");
    }

    // ---- Expiry ----

    [Fact]
    public void Generate_ExpiryIsInTheFuture()
    {
        var token = MakeService().Generate(MakeUser());
        var jwt   = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.ValidTo.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public void Generate_ExpiryIsAboutThirtyDaysFromNow()
    {
        var token = MakeService().Generate(MakeUser());
        var jwt   = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.ValidTo.Should().BeCloseTo(DateTime.UtcNow.AddDays(30), TimeSpan.FromMinutes(1));
    }

    // ---- Uniqueness ----

    [Fact]
    public void Generate_DifferentUsersGetDifferentTokens()
    {
        var svc = MakeService();
        var t1  = svc.Generate(MakeUser("alice"));
        var t2  = svc.Generate(MakeUser("bob"));
        t1.Should().NotBe(t2);
    }
}
