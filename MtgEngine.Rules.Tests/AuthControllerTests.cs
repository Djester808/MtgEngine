using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc;
using MtgEngine.Api.Controllers;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;
using Xunit;

namespace MtgEngine.Rules.Tests;

public sealed class AuthControllerTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MtgEngineDbContext> _dbOptions;
    private MtgEngineDbContext _db = null!;
    private AuthController _controller = null!;

    public AuthControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<MtgEngineDbContext>()
            .UseSqlite(_connection)
            .Options;
    }

    public async Task InitializeAsync()
    {
        _db = new MtgEngineDbContext(_dbOptions);
        await _db.Database.EnsureCreatedAsync();

        var tokens = new TokenService(
            new ConfigurationBuilder()
                .AddInMemoryCollection([new("Jwt:Secret", "test-jwt-secret-key-32-chars-min!!")])
                .Build());

        _controller = new AuthController(_db, tokens);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _connection.Dispose();
    }

    // ---- Register ----

    [Fact]
    public async Task Register_WithValidData_ReturnsOkWithToken()
    {
        var result = await _controller.Register(
            new RegisterRequest("alice", "alice@example.com", "password123"));

        result.Result.Should().BeOfType<OkObjectResult>();
        var body = ((OkObjectResult)result.Result!).Value as AuthTokenResponse;
        body!.Token.Should().NotBeNullOrEmpty();
        body.Username.Should().Be("alice");
    }

    [Fact]
    public async Task Register_PersistsUserToDatabase()
    {
        await _controller.Register(
            new RegisterRequest("bob", "bob@example.com", "password123"));

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == "bob");
        user.Should().NotBeNull();
        user!.Email.Should().Be("bob@example.com");
    }

    [Fact]
    public async Task Register_StoresHashedPassword_NotPlaintext()
    {
        await _controller.Register(
            new RegisterRequest("carol", "carol@example.com", "secret"));

        var user = await _db.Users.FirstAsync(u => u.Username == "carol");
        user.PasswordHash.Should().NotBe("secret");
        user.PasswordHash.Should().Contain(":"); // salt:hash format
    }

    [Fact]
    public async Task Register_WithDuplicateUsername_ReturnsConflict()
    {
        await _controller.Register(
            new RegisterRequest("dave", "dave@example.com", "password123"));

        var result = await _controller.Register(
            new RegisterRequest("dave", "dave2@example.com", "password123"));

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_ReturnsConflict()
    {
        await _controller.Register(
            new RegisterRequest("eve", "shared@example.com", "password123"));

        var result = await _controller.Register(
            new RegisterRequest("eve2", "shared@example.com", "password123"));

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Register_WithShortPassword_ReturnsBadRequest()
    {
        var result = await _controller.Register(
            new RegisterRequest("frank", "frank@example.com", "abc"));

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Register_WithEmptyUsername_ReturnsBadRequest()
    {
        var result = await _controller.Register(
            new RegisterRequest("", "noname@example.com", "password123"));

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Register_WithEmptyEmail_ReturnsBadRequest()
    {
        var result = await _controller.Register(
            new RegisterRequest("grace", "", "password123"));

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Register_TokenContainsCorrectUserId()
    {
        var result = await _controller.Register(
            new RegisterRequest("henry", "henry@example.com", "password123"));

        var token = ((OkObjectResult)result.Result!).Value as AuthTokenResponse;
        var jwt   = new JwtSecurityTokenHandler().ReadJwtToken(token!.Token);
        var sub   = jwt.Claims.First(c => c.Type is "nameid" or ClaimTypes.NameIdentifier).Value;

        var dbUser = await _db.Users.FirstAsync(u => u.Username == "henry");
        sub.Should().Be(dbUser.Id.ToString());
    }

    // ---- Login ----

    [Fact]
    public async Task Login_WithCorrectUsername_ReturnsOkWithToken()
    {
        await SeedUser("ivan", "ivan@example.com", "password");

        var result = await _controller.Login(new LoginRequest("ivan", "password"));

        result.Result.Should().BeOfType<OkObjectResult>();
        var body = ((OkObjectResult)result.Result!).Value as AuthTokenResponse;
        body!.Token.Should().NotBeNullOrEmpty();
        body.Username.Should().Be("ivan");
    }

    [Fact]
    public async Task Login_WithEmail_ReturnsOkWithToken()
    {
        await SeedUser("jane", "jane@example.com", "password");

        var result = await _controller.Login(new LoginRequest("jane@example.com", "password"));

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsUnauthorized()
    {
        await SeedUser("kate", "kate@example.com", "correct");

        var result = await _controller.Login(new LoginRequest("kate", "wrong"));

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Login_WithNonexistentUser_ReturnsUnauthorized()
    {
        var result = await _controller.Login(new LoginRequest("nobody", "password"));

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Login_TokenContainsCorrectUserId()
    {
        var user = await SeedUser("leo", "leo@example.com", "password");

        var result  = await _controller.Login(new LoginRequest("leo", "password"));
        var token   = ((OkObjectResult)result.Result!).Value as AuthTokenResponse;
        var jwt     = new JwtSecurityTokenHandler().ReadJwtToken(token!.Token);
        var sub     = jwt.Claims.First(c => c.Type is "nameid" or ClaimTypes.NameIdentifier).Value;

        sub.Should().Be(user.Id.ToString());
    }

    // ---- Helper ----

    private async Task<User> SeedUser(string username, string email, string password)
    {
        var user = new User
        {
            Username     = username,
            Email        = email,
            PasswordHash = PasswordHasher.Hash(password),
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }
}
