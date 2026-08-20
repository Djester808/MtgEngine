using System.Collections.Immutable;

namespace MtgEngine.Rules.State;

/// <summary>
/// One player, and the three zones that belong to them (CR 400.1).
/// </summary>
/// <remarks>
/// The zone lists hold ids, not objects; the objects themselves live in one dictionary on
/// <see cref="GameState"/> so that "where is this object" has a single answer.
/// <para>
/// <b>Index 0 is the top of every ordered zone.</b> Drawing takes <c>Library[0]</c>, a card put
/// into a graveyard goes to <c>Graveyard[0]</c> (CR 404.1: "put on top of its owner's
/// graveyard"), and the stack resolves <c>Stack[0]</c>. One convention everywhere beats a
/// per-zone rule nobody can recall at the call site.
/// </para>
/// </remarks>
public sealed record PlayerState
{
    public required Guid PlayerId { get; init; }

    public required string Name { get; init; }

    /// <summary>CR 119. Starting total is set by the format, not by this record.</summary>
    public int Life { get; init; }

    /// <summary>CR 122.1a. Ten or more is a loss, checked as a state-based action (CR 704.5c).</summary>
    public int PoisonCounters { get; init; }

    /// <summary>Face-down, order fixed, top at index 0 (CR 401.1, 401.2).</summary>
    public ImmutableList<ObjectId> Library { get; init; } = [];

    /// <summary>Hidden from every other player (CR 400.2, 402.3).</summary>
    public ImmutableList<ObjectId> Hand { get; init; } = [];

    /// <summary>Face-up, most recently added at index 0 (CR 404.1, 404.2).</summary>
    public ImmutableList<ObjectId> Graveyard { get; init; } = [];

    /// <summary>
    /// Set the moment a draw from an empty library is attempted, and read by state-based
    /// actions at the next check (CR 104.3c, 704.5b).
    /// </summary>
    /// <remarks>
    /// It is a flag and not a computed property because the rule is about an <em>attempt</em>
    /// that already happened, not about the library being empty now — a player at zero cards
    /// who has not yet been asked to draw has not lost. The engine this replaces wrote
    /// <c>Library.IsEmpty &amp;&amp; false</c> with a comment saying the real check lived in the
    /// rules engine. It did not live anywhere.
    /// </remarks>
    public bool HasAttemptedDrawFromEmptyLibrary { get; init; }

    /// <summary>Set by state-based actions once this player has lost (CR 104.2, 704.5a-c).</summary>
    public bool HasLost { get; init; }

    /// <summary>
    /// Damage dealt by a source with deathtouch since the last state-based action check
    /// (CR 704.5h) is tracked on the permanent, not here; this records why the player lost so a
    /// client can say so.
    /// </summary>
    public string? LossReason { get; init; }

    /// <summary>
    /// Lands played this turn, against the one-per-turn allowance (CR 305.2, 505.6b). A count
    /// rather than a bool because effects raise the allowance.
    /// </summary>
    public int LandsPlayedThisTurn { get; init; }

    // Records compare collections by reference; see Structural.
    public bool Equals(PlayerState? other) =>
        other is not null &&
        PlayerId == other.PlayerId &&
        string.Equals(Name, other.Name, StringComparison.Ordinal) &&
        Life == other.Life &&
        PoisonCounters == other.PoisonCounters &&
        HasAttemptedDrawFromEmptyLibrary == other.HasAttemptedDrawFromEmptyLibrary &&
        HasLost == other.HasLost &&
        string.Equals(LossReason, other.LossReason, StringComparison.Ordinal) &&
        LandsPlayedThisTurn == other.LandsPlayedThisTurn &&
        Structural.Same(Library, other.Library) &&
        Structural.Same(Hand, other.Hand) &&
        Structural.Same(Graveyard, other.Graveyard);

    public override int GetHashCode() =>
        HashCode.Combine(PlayerId, Life, PoisonCounters, Library.Count, Hand.Count, Graveyard.Count);
}
