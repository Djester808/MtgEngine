using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MtgEngine.Api.Data;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Domain.Models;

/// <summary>Isolated lobby fixtures using production services and a private in-memory database.</summary>
public static class QaLobby
{
    public static Guid Account(int seat) => Guid.Parse($"00000000-0000-0000-0000-{seat + 1:D12}");

    public static async Task ConfigureAsync(IServiceCollection services, QaPool pool)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        services.AddSingleton(connection);
        services.AddDbContext<MtgEngineDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton<IGameLobbyStore, GameLobbyStore>();
        services.AddSingleton<IGameLobbyDecks>(new QaLobbyDecks(pool));
        services.AddScoped<GameLobbyService>();
        services.AddScoped<GameLobbyStartup>();
        // Initialize through a temporary context; the shared connection stays alive with the host.
        await using var db = new MtgEngineDbContext(new DbContextOptionsBuilder<MtgEngineDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        for (var seat = 0; seat < 4; seat++)
            db.Users.Add(new User { Id = Account(seat), Username = $"qa{seat}", Email = $"qa{seat}@example.test" });
        await db.SaveChangesAsync();
    }

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/games/lobbies").RequireAuthorization();
        group.MapGet("/", (ClaimsPrincipal user, GameLobbyService service) => service.ListAsync(Actor(user)));
        group.MapGet("/players", (string? search, ClaimsPrincipal user, GameLobbyService service) => service.PlayersAsync(Actor(user), search));
        group.MapGet("/decks", (ClaimsPrincipal user, IGameLobbyDecks decks) => decks.ListAsync(Actor(user)));
        group.MapGet("/{id:guid}", (Guid id, ClaimsPrincipal user, GameLobbyService service) => service.GetAsync(id, Actor(user)));
        group.MapPost("/", (CreateGameLobbyRequest request, ClaimsPrincipal user, GameLobbyService service) => service.CreateAsync(Actor(user), request));
        group.MapPost("/{id:guid}/deck", (Guid id, BringLobbyDeckRequest request, ClaimsPrincipal user, GameLobbyService service) => service.BringDeckAsync(id, Actor(user), request));
        group.MapPost("/{id:guid}/leave", (Guid id, LobbyRevisionRequest request, ClaimsPrincipal user, GameLobbyService service) => service.LeaveAsync(id, Actor(user), request.Revision));
        group.MapPost("/{id:guid}/start", (Guid id, LobbyRevisionRequest request, ClaimsPrincipal user, GameLobbyStartup service) => service.StartAsync(id, Actor(user), request.Revision));
    }

    private static string Actor(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private sealed class QaLobbyDecks(QaPool pool) : IGameLobbyDecks
    {
        public Task<IReadOnlyList<LobbyDeckChoice>> ListAsync(string account, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LobbyDeckChoice>>([new(Guid.Parse(account), "Practice deck", "commander", 73)]);

        public Task<LobbyDeck> LoadAsync(Guid deckId, string account, string format, bool allowHonourSystem, CancellationToken ct = default)
        {
            if (deckId.ToString() != account || format != "commander")
                throw new ResourceNotFoundException("Deck not found.");
            return Task.FromResult(new LobbyDeck(deckId, "Practice deck", Enumerable.Range(0, 8).SelectMany(_ => pool.Deck)
                .Select(id => new MtgEngine.Rules.State.StartingCard(id)).ToArray(), ["leader"]));
        }
    }
}
