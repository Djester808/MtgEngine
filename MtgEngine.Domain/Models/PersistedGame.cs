namespace MtgEngine.Domain.Models;

/// <summary>
/// One game's event log, stored so a restart, a crash or a reconnect does not lose the table.
/// </summary>
/// <remarks>
/// The state is not stored: it is a fold of the log (CR has no opinion on this, but the engine
/// does — <c>GameReducer.Replay</c> is the only way a state comes into being outside a command).
/// Storing the fold as well would give two sources of truth that can disagree, and the one that
/// is wrong would be the one nobody looks at. The log is appended to per applied command.
/// <para>
/// This model is in the Domain project, so it may not name an engine type: the log is the JSON
/// the engine's own serializer wrote, and the seats are JSON too.
/// </para>
/// </remarks>
public sealed class PersistedGame
{
    public Guid Id { get; set; }

    /// <summary>The format the game was created in, kept so a replay rebuilds the same setup.</summary>
    public string Format { get; set; } = "commander";

    /// <summary>Whether this table admits cards the engine cannot enforce. Chosen at creation, never after.</summary>
    public bool AllowHonourSystem { get; set; }

    /// <summary>
    /// The account signed in at each seat, as a JSON array of ids, index = seat number.
    /// </summary>
    /// <remarks>
    /// This is the only thing that decides which seat a caller holds. A user id in a request is
    /// never consulted, so a client cannot act as its opponent by naming them.
    /// </remarks>
    public string SeatsJson { get; set; } = "[]";

    /// <summary>The event log, as written by the engine's log serializer.</summary>
    public string LogJson { get; set; } = "[]";

    /// <summary>How many events the log holds, so a report can name a position without parsing it.</summary>
    public int EventCount { get; set; }

    public bool Finished { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
