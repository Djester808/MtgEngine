using System.Collections.Concurrent;
using MtgEngine.Domain.Models;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.ValueObjects;
using MtgEngine.Rules;

namespace MtgEngine.Api.Services;

/// <summary>
/// In-memory store for all live game sessions.
/// Singleton — injected into controllers and the SignalR hub.
/// </summary>
public sealed class GameSessionService : IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<Guid, GameSession> _sessions = new();
    private readonly ILogger<GameSessionService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private Timer? _cleanupTimer;

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(2);

    public GameSessionService(
        ILogger<GameSessionService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger      = logger;
        _scopeFactory = scopeFactory;
    }

    // ---- IHostedService -----------------------------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer = new Timer(
            CleanupExpiredSessions,
            null,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(15));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    // ---- Create / Get -------------------------------------

    /// <summary>
    /// Creates a new game from two player names and deck presets.
    /// Returns the session (with tokens) for the response.
    /// </summary>
    public async Task<GameSession> CreateAsync(
        string player1Name,
        string player2Name,
        string[] player1DeckPresets,
        string[] player2DeckPresets)
    {
        var player1Id = Guid.NewGuid();
        var player2Id = Guid.NewGuid();

        using var scope = _scopeFactory.CreateScope();
        var deckBuilder = scope.ServiceProvider.GetRequiredService<IDeckBuilderService>();
        var deck1 = await deckBuilder.BuildDeckAsync(player1DeckPresets, player1Id);
        var deck2 = await deckBuilder.BuildDeckAsync(player2DeckPresets, player2Id);

        var initialState = GameEngine.CreateGame(
            player1Id, player1Name, deck1,
            player2Id, player2Name, deck2);

        var session = new GameSession(initialState, player1Id, player2Id);
        _sessions[session.GameId] = session;

        _logger.LogInformation("Game {GameId} created: {P1} vs {P2}", session.GameId, player1Name, player2Name);
        return session;
    }

    public GameSession? Get(Guid gameId) =>
        _sessions.TryGetValue(gameId, out var session) ? session : null;

    public GameSession GetOrThrow(Guid gameId) =>
        Get(gameId) ?? throw new KeyNotFoundException($"Game {gameId} not found.");

    // ---- Cleanup ------------------------------------------

    private void CleanupExpiredSessions(object? state)
    {
        var expired = _sessions.Values
            .Where(s => s.IsExpired(IdleTimeout))
            .Select(s => s.GameId)
            .ToList();

        foreach (var id in expired)
        {
            _sessions.TryRemove(id, out _);
            _logger.LogInformation("Game {GameId} expired and removed.", id);
        }
    }

    public void Dispose() => _cleanupTimer?.Dispose();
}
