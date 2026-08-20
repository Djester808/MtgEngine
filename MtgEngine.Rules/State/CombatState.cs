using System.Collections.Immutable;

namespace MtgEngine.Rules.State;

/// <summary>
/// Who is attacking whom, and who is blocking what (CR 506–511).
/// </summary>
/// <remarks>
/// Blocked-ness is recorded rather than derived. CR 509.1h: a creature with blockers declared for
/// it becomes blocked and "remains blocked even if all the creatures blocking it are removed from
/// combat" — so an attacker whose only blocker died is still blocked and still deals no damage to
/// the player. Deriving it from "does anything currently block me" gets that backwards, and gets
/// it backwards in the attacker's favour.
/// </remarks>
public sealed record CombatState
{
    /// <summary>Each attacking creature and the player it is attacking (CR 508.1b).</summary>
    public ImmutableDictionary<ObjectId, Guid> Attackers { get; init; } =
        ImmutableDictionary<ObjectId, Guid>.Empty;

    /// <summary>
    /// Each attacker and the creatures blocking it, in the order its controller assigns damage
    /// (CR 510.1c). The list order is the damage assignment order.
    /// </summary>
    public ImmutableDictionary<ObjectId, ImmutableList<ObjectId>> Blockers { get; init; } =
        ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty;

    /// <summary>
    /// Attackers that had blockers declared for them, whatever happened to those blockers since
    /// (CR 509.1h).
    /// </summary>
    public ImmutableHashSet<ObjectId> Blocked { get; init; } = [];

    /// <summary>Set once the active player has declared, even if they declared nothing.</summary>
    public bool AttackersDeclared { get; init; }

    /// <summary>Set once the defending player has declared, even if they declared nothing.</summary>
    public bool BlockersDeclared { get; init; }

    /// <summary>
    /// How many combat damage steps have happened. First or double strike gives the phase a
    /// second one (CR 510.4).
    /// </summary>
    public int DamageStepsDone { get; init; }

    /// <summary>Whether anything is attacking, which decides whether later steps happen at all.</summary>
    public bool AnyAttackers => !Attackers.IsEmpty;

    /// <summary>The creatures blocking a given attacker, in damage assignment order.</summary>
    public ImmutableList<ObjectId> BlockersOf(ObjectId attacker) =>
        Blockers.TryGetValue(attacker, out var list) ? list : [];

    /// <summary>Whether this creature is blocking anything.</summary>
    public bool IsBlocking(ObjectId creature) =>
        Blockers.Values.Any(list => list.Contains(creature));

    /// <summary>The attackers a given blocker is blocking.</summary>
    public IEnumerable<ObjectId> BlockedBy(ObjectId blocker) =>
        Blockers.Where(kv => kv.Value.Contains(blocker)).Select(kv => kv.Key);

    public bool Equals(CombatState? other) =>
        other is not null &&
        AttackersDeclared == other.AttackersDeclared &&
        BlockersDeclared == other.BlockersDeclared &&
        DamageStepsDone == other.DamageStepsDone &&
        Blocked.SetEquals(other.Blocked) &&
        Structural.Same(Attackers, other.Attackers) &&
        Attackers.Count == other.Attackers.Count &&
        Blockers.Count == other.Blockers.Count &&
        Blockers.All(kv =>
            other.Blockers.TryGetValue(kv.Key, out var theirs) && Structural.Same(kv.Value, theirs));

    public override int GetHashCode() =>
        HashCode.Combine(
            Attackers.Count, Blockers.Count, Blocked.Count,
            AttackersDeclared, BlockersDeclared, DamageStepsDone);
}
