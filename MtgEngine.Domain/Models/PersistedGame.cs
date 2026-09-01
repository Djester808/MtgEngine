namespace MtgEngine.Domain.Models;

/// <summary>
/// A game in progress, stored as the events that produced it.
/// </summary>
/// <remarks>
/// There is no column for whose turn it is, what is on the battlefield, or who has priority,
/// because a game's state is a fold of its log — storing both would create two accounts of one
/// game that could disagree, and the reconciling code would be the bug.
/// <para>
/// <see cref="IsOver"/> and <see cref="LastActivityUtc"/> are the exceptions, and they are here
/// for housekeeping rather than for play: sweeping abandoned games has to be able to ask those
/// two questions without replaying every log on disk.
/// </para>
/// </remarks>
public sealed class PersistedGame
{
    public Guid GameId { get; set; }

    /// <summary>The event log, as <c>EventLogSerializer</c> writes it.</summary>
    public string Log { get; set; } = string.Empty;

    /// <summary>When the game last saw an action, for the idle sweep.</summary>
    public DateTime LastActivityUtc { get; set; }

    /// <summary>
    /// Whether the game has finished (CR 104.1).
    /// </summary>
    /// <remarks>
    /// A finished game is kept rather than deleted — its log is the record of the match, and the
    /// players may want to read it — but it is never rehydrated into a playable session.
    /// </remarks>
    public bool IsOver { get; set; }
}
