using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>
/// Represents one live game session in memory.
/// Holds the current game state, player tokens, and session metadata.
/// </summary>
public sealed class GameSession
{
    public Guid GameId { get; } = Guid.NewGuid();
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; private set; } = DateTime.UtcNow;

    public string Player1Token { get; } = Guid.NewGuid().ToString("N");
    public string Player2Token { get; } = Guid.NewGuid().ToString("N");

    public Guid Player1Id { get; init; }
    public Guid Player2Id { get; init; }

    // Token -> PlayerId lookup
    public Dictionary<string, Guid> TokenToPlayerId { get; private set; } = [];

    // Current game state (immutable record, replaced on every action)
    private GameState _state;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public GameSession(GameState initialState, Guid player1Id, Guid player2Id)
    {
        _state    = initialState;
        Player1Id = player1Id;
        Player2Id = player2Id;

        TokenToPlayerId = new Dictionary<string, Guid>
        {
            [Player1Token] = player1Id,
            [Player2Token] = player2Id,
        };
    }

    public GameState State => _state;

    /// <summary>
    /// Applies a rules action under the session lock.
    /// Returns (stateBefore, stateAfter) for diff generation.
    /// </summary>
    public async Task<(GameState Before, GameState After)> ApplyAsync(
        Func<GameState, GameState> action)
    {
        await _lock.WaitAsync();
        try
        {
            var before = _state;
            var after  = action(_state);
            _state = after;
            LastActivityAt = DateTime.UtcNow;
            return (before, after);
        }
        finally
        {
            _lock.Release();
        }
    }

    public bool TryResolveToken(string token, out Guid playerId) =>
        TokenToPlayerId.TryGetValue(token, out playerId);

    public bool IsExpired(TimeSpan idleTimeout) =>
        DateTime.UtcNow - LastActivityAt > idleTimeout;
}
