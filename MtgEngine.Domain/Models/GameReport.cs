namespace MtgEngine.Domain.Models;

/// <summary>
/// A player saying "that card did not do what it says" from a position in a game's log.
/// </summary>
/// <remarks>
/// The last line of verification and the only one that sees real play. The pipeline turns a
/// report into a scenario candidate for triage: the log up to <see cref="EventIndex"/> is the
/// board, and the card named by <see cref="OracleId"/> is what was played. Nothing here is
/// judged at the time it is filed — a report is evidence, not a verdict.
/// </remarks>
public sealed class GameReport
{
    public Guid Id { get; set; }

    public Guid GameId { get; set; }

    /// <summary>The account that filed it, taken from its own token.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The position in the game's event log the report is about.</summary>
    public int EventIndex { get; set; }

    public string OracleId { get; set; } = string.Empty;

    /// <summary>What the player says went wrong. Free text; the pipeline reads it, the engine never does.</summary>
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
