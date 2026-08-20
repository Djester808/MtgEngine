using System.Collections.Immutable;

namespace MtgEngine.Rules.State;

/// <summary>What kind of decision the game is waiting on.</summary>
/// <remarks>
/// Each value names a place the rules say a player chooses. The engine used to answer these
/// itself — keeping the oldest legend, taking replacement effects in timestamp order, putting a
/// player's triggers on the stack in the order they happened — and each of those was a comment
/// saying "until there is a way to ask them". This is the way to ask them.
/// </remarks>
public enum ChoiceKind
{
    /// <summary>Whether to take a mulligan (CR 103.5).</summary>
    Mulligan,

    /// <summary>Which cards to put on the bottom after a mulligan (CR 103.5).</summary>
    BottomAfterMulligan,

    /// <summary>Which duplicate legendary permanent to keep (CR 704.5j).</summary>
    LegendRule,

    /// <summary>The order to put your simultaneous triggers on the stack (CR 603.3b).</summary>
    OrderTriggers,

    /// <summary>Which applicable replacement effect to apply next (CR 616.1).</summary>
    OrderReplacements,

    /// <summary>How to divide an attacker's damage among its blockers (CR 510.1c).</summary>
    DivideCombatDamage,
}

/// <summary>One thing a player may pick.</summary>
public sealed record ChoiceOption(string Id, string Label)
{
    /// <summary>
    /// For a division choice, how much is being assigned to this option (CR 510.1c).
    /// </summary>
    /// <remarks>
    /// Carried on the option rather than in a parallel list so an answer cannot pair the wrong
    /// number with the wrong target.
    /// </remarks>
    public int Amount { get; init; }
}

/// <summary>
/// A decision the game is waiting on, and cannot proceed without.
/// </summary>
/// <remarks>
/// The game stops here. Nothing else may happen while a choice is outstanding — not another
/// player's action, not a state-based action, not a step ending — because everything downstream
/// depends on the answer. That is exactly how the rules work: the game does not carry on around
/// a player who has not decided.
/// <para>
/// The engine's alternative was to answer for them, which it did in three places and which is
/// wrong in a way nobody would ever see: keeping the older legend is a legal choice, so a player
/// robbed of the decision gets a legal game that is not the one they would have played.
/// </para>
/// </remarks>
public sealed record PendingChoice
{
    public required string Id { get; init; }

    /// <summary>Who has to answer. Only they may.</summary>
    public required Guid PlayerId { get; init; }

    public required ChoiceKind Kind { get; init; }

    /// <summary>What to show the player.</summary>
    public required string Prompt { get; init; }

    public ImmutableList<ChoiceOption> Options { get; init; } = [];

    /// <summary>Fewest options that may be picked.</summary>
    public int MinPicks { get; init; } = 1;

    /// <summary>Most options that may be picked. Equal to the option count for an ordering.</summary>
    public int MaxPicks { get; init; } = 1;

    /// <summary>
    /// True when the answer is a sequence rather than a set — the order of the picks is the
    /// answer (CR 603.3b, 616.1).
    /// </summary>
    public bool IsOrdering => Kind is ChoiceKind.OrderTriggers or ChoiceKind.OrderReplacements;

    /// <summary>Extra state the resumption needs, opaque to everyone else.</summary>
    /// <remarks>
    /// Only ever ids the engine put there itself — the attacker whose damage is being divided,
    /// the permanent that triggered the legend rule. It is in state rather than in a field on
    /// <see cref="Engine.Game"/> so a replayed log rebuilds a game that is mid-question exactly
    /// as it was.
    /// </remarks>
    public ImmutableList<string> Context { get; init; } = [];

    public bool Equals(PendingChoice? other) =>
        other is not null &&
        string.Equals(Id, other.Id, StringComparison.Ordinal) &&
        PlayerId == other.PlayerId &&
        Kind == other.Kind &&
        MinPicks == other.MinPicks &&
        MaxPicks == other.MaxPicks &&
        Structural.Same(Options, other.Options) &&
        Structural.Same(Context, other.Context);

    public override int GetHashCode() =>
        HashCode.Combine(Id, PlayerId, Kind, Options.Count, MinPicks, MaxPicks);
}
