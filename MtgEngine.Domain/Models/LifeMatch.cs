namespace MtgEngine.Domain.Models;

/// <summary>
/// How a player left a game, per CR 104.3.
/// </summary>
/// <remarks>
/// Only the reasons a life counter can actually observe. The counter watches life, poison
/// and commander damage itself (they are state-based actions it has the numbers for — CR
/// 704.5a, 704.5c, 903.10a); the rest are what a player tells it happened.
/// </remarks>
public enum MatchLossReason
{
    /// <summary>Not recorded — the seat lost and nobody said how.</summary>
    Unspecified = 0,

    /// <summary>Life total 0 or less. CR 104.3b / 704.5a.</summary>
    LifeTotal,

    /// <summary>Ten or more poison counters. CR 104.3d / 704.5c / 122.1f.</summary>
    Poison,

    /// <summary>21 or more combat damage from one commander. CR 104.3j / 903.10a.</summary>
    CommanderDamage,

    /// <summary>Drew from an empty library. CR 104.3c / 704.5b.</summary>
    EmptyLibrary,

    /// <summary>An effect said so — Approach of the Second Sun on the other side, and the like. CR 104.3e.</summary>
    Effect,

    /// <summary>Conceded. CR 104.3a.</summary>
    Conceded,
}

/// <summary>
/// One finished game, as reported by the life counter.
/// </summary>
/// <remarks>
/// This is a **self-reported** record of a game played on a table, not an engine transcript:
/// nothing here was refereed. What keeps it honest enough to be worth storing is the
/// attribution rule in <see cref="LifeMatchSeat.UserId"/> — a seat is only tied to an account
/// when that account signed in on the device, so the worst a fabricated match can do is
/// distort the fabricator's own record.
/// </remarks>
public sealed class LifeMatch
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Who sent it. Kept so an abusive stream of records has an owner.</summary>
    public Guid RecordedByUserId { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The life total the game started at — 20, 40, or whatever the table agreed.</summary>
    public int StartingLife { get; set; }

    public ICollection<LifeMatchSeat> Seats { get; set; } = [];
}

/// <summary>One player's place in a <see cref="LifeMatch"/>.</summary>
public sealed class LifeMatchSeat
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MatchId { get; set; }

    public LifeMatch? Match { get; set; }

    /// <summary>Position at the table, 0-based. Unique within a match.</summary>
    public int Seat { get; set; }

    /// <summary>
    /// The account this seat belongs to, or null for a guest.
    /// </summary>
    /// <remarks>
    /// Set **only** from a token the seat's own player supplied by signing in on the counter.
    /// A caller cannot name somebody else's user id and have it stored, which is the whole
    /// reason win/loss records are worth anything here.
    /// </remarks>
    public Guid? UserId { get; set; }

    /// <summary>What the counter showed above this seat. A guest has only this.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public bool Won { get; set; }

    public MatchLossReason LossReason { get; set; } = MatchLossReason.Unspecified;

    /// <summary>Life when the seat left the game. Can be negative — that is usually the point.</summary>
    public int FinalLife { get; set; }
}
