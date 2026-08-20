using System.Collections.Immutable;

namespace MtgEngine.Rules.State;

/// <summary>
/// Who may act, and who has declined since the last thing happened (CR 117).
/// </summary>
/// <remarks>
/// The second half is the part the previous engine did not have. CR 117.4 turns on players
/// passing <em>in succession</em> — passing with nothing happening in between — so the engine has
/// to remember who has passed since the last resolution or action, not merely whose turn it is
/// to speak. That engine instead asked "is the passer the active player?" and branched, which
/// gives the right answer in a duel by coincidence and no answer at all at three players.
/// <para>
/// Nothing here mentions a count of players. All players pass in succession when
/// <see cref="Passed"/> covers everyone still in the game, whether that is two or six.
/// </para>
/// </remarks>
public sealed record PriorityState
{
    /// <summary>
    /// The player who may act, or null when nobody has priority — during the untap step
    /// (CR 502.4), during cleanup (CR 514.3), and while a spell or ability resolves (CR 117.2e).
    /// </summary>
    public Guid? Holder { get; init; }

    /// <summary>
    /// Players who have passed since the last spell resolved or action was taken (CR 117.4).
    /// </summary>
    /// <remarks>
    /// Cleared by anything that happens, which is what "in succession" means: casting a spell
    /// puts everyone back to needing another chance to respond (CR 117.3c).
    /// </remarks>
    public ImmutableHashSet<Guid> Passed { get; init; } = [];

    /// <summary>True when every player still in the game has passed without acting (CR 117.4).</summary>
    public bool AllPassed(IEnumerable<Guid> playersStillIn) =>
        playersStillIn.All(Passed.Contains);

    public bool Equals(PriorityState? other) =>
        other is not null &&
        Holder == other.Holder &&
        Passed.SetEquals(other.Passed);

    public override int GetHashCode() => HashCode.Combine(Holder, Passed.Count);
}

/// <summary>
/// An ability that has triggered but is not on the stack yet (CR 603.2, 603.3).
/// </summary>
/// <remarks>
/// "Nothing actually happens at the time an ability triggers" (CR 117.2a). It waits here until
/// the next time a player would receive priority, and only then goes on the stack — which is why
/// a creature can trigger something and die before that trigger resolves, and why the trigger
/// still happens.
/// <para>
/// It holds an id rather than a delegate so that state stays foldable from a log and comparable
/// by value. The definition is found again through <see cref="Abilities.IAbilitySource"/>.
/// </para>
/// </remarks>
public sealed record PendingTrigger
{
    public required ObjectId SourceId { get; init; }

    public required string AbilityId { get; init; }

    public required string Text { get; init; }

    /// <summary>
    /// Who controls the trigger: whoever controlled its source when it triggered (CR 603.3).
    /// Recorded now because the source may be gone by the time it goes on the stack.
    /// </summary>
    public required Guid ControllerId { get; init; }
}
