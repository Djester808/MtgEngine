using System.Collections.Immutable;
using MtgEngine.Domain.Models;

namespace MtgEngine.Rules.State;

/// <summary>
/// Identity of one object in one zone at one time.
/// </summary>
/// <remarks>
/// Deliberately not the card's identity. CR 400.7: "An object that moves from one zone to
/// another becomes a new object with no memory of, or relation to, its previous existence."
/// A creature that dies and is returned is a different object from the one that died, and an
/// engine that reuses an id there will happily let a dead creature's aura reattach to it.
/// <para>
/// A struct wrapper rather than a bare <see cref="Guid"/> so a permanent id and a player id
/// cannot be passed to each other's parameters.
/// </para>
/// </remarks>
public readonly record struct ObjectId(Guid Value)
{
    public static ObjectId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N")[..8];
}

/// <summary>
/// The part of an object that only exists while it is on the battlefield (CR 403.3: every
/// object on the battlefield is a permanent).
/// </summary>
/// <remarks>
/// Everything here is <em>status</em> — what has been done to the permanent. None of it is a
/// characteristic. Power, toughness, types, colours and abilities are never stored: they are
/// computed from the printed card plus the continuous effects that apply, every time they are
/// asked for (CR 613). The engine this replaces stored them and wrote static abilities into
/// state as mutations, which is why a buff outlived the lord that granted it.
/// </remarks>
public sealed record PermanentState
{
    /// <summary>CR 701.26a. Untapped is the default; nothing enters tapped without an effect.</summary>
    public bool IsTapped { get; init; }

    /// <summary>
    /// Set while the permanent has not been controlled continuously since its controller's most
    /// recent turn began (CR 302.6). Named for the rule, not for the folklore: it gates attacking
    /// and {T} costs, and it applies to every permanent type, not only creatures.
    /// </summary>
    public bool HasSummoningSickness { get; init; } = true;

    /// <summary>
    /// Damage marked on the permanent this turn (CR 120.3). Cleared during cleanup (CR 514.2),
    /// not when it is dealt, and compared against toughness by state-based actions.
    /// </summary>
    public int DamageMarked { get; init; }

    /// <summary>
    /// Counters on the permanent, by kind (CR 122). "+1/+1" and "-1/-1" are the common two and
    /// annihilate each other as a state-based action (CR 704.5q), which is slice 3's business.
    /// </summary>
    public ImmutableDictionary<string, int> Counters { get; init; } =
        ImmutableDictionary<string, int>.Empty;

    // Records compare collections by reference; see Structural.
    public bool Equals(PermanentState? other) =>
        other is not null &&
        IsTapped == other.IsTapped &&
        HasSummoningSickness == other.HasSummoningSickness &&
        DamageMarked == other.DamageMarked &&
        Structural.Same(Counters, other.Counters);

    public override int GetHashCode() =>
        HashCode.Combine(IsTapped, HasSummoningSickness, DamageMarked, Counters.Count);
}

/// <summary>
/// An ability waiting on the stack, which is an object but not a card (CR 113.7a, 603.3).
/// </summary>
public sealed record AbilityOnStack
{
    /// <summary>The object whose ability this is. It may already have left the battlefield.</summary>
    public required ObjectId SourceId { get; init; }

    /// <summary>Which of the source's abilities, by the id its definition carries.</summary>
    public required string AbilityId { get; init; }

    /// <summary>The ability's text — everything it has (CR 405.4).</summary>
    public required string Text { get; init; }
}

/// <summary>
/// One object in the game: a card in a zone, a permanent on the battlefield, or an ability on
/// the stack.
/// </summary>
public sealed record GameObject
{
    /// <summary>Identity in the current zone. Replaced on every zone change (CR 400.7).</summary>
    public required ObjectId Id { get; init; }

    /// <summary>
    /// The printed card. Its characteristics are the starting point for every calculation and
    /// are never edited — effects layer over them (CR 613) rather than rewriting them here.
    /// </summary>
    public required CardDefinition Card { get; init; }

    /// <summary>
    /// The player who started the game with this card (CR 108.3). Never changes, whatever
    /// happens to control of it, and decides which graveyard, hand, or library it returns to
    /// (CR 400.3).
    /// </summary>
    public required Guid OwnerId { get; init; }

    /// <summary>
    /// Who currently controls it (CR 108.4). Equal to the owner until an effect says otherwise.
    /// Objects in a hidden or owner-specific zone are controlled by their owner.
    /// </summary>
    public required Guid ControllerId { get; init; }

    /// <summary>Where it is now.</summary>
    public required Zone Zone { get; init; }

    /// <summary>
    /// When this object came into being, as a monotonic counter rather than a clock.
    /// </summary>
    /// <remarks>
    /// CR 613.7: continuous effects are applied in timestamp order within a layer, so this has
    /// to be a total order over everything in the game. A wall clock is not one — two objects
    /// entering in the same tick would tie, and the tie-break would be arbitrary. The counter
    /// lives on <see cref="GameState"/> and only ever goes up.
    /// </remarks>
    public required long Timestamp { get; init; }

    /// <summary>Non-null exactly while <see cref="Zone"/> is <see cref="Zone.Battlefield"/>.</summary>
    public PermanentState? Permanent { get; init; }

    /// <summary>
    /// Non-null when this object is an ability on the stack rather than a card (CR 113.7a).
    /// </summary>
    /// <remarks>
    /// An ability on the stack "has the text of the ability that created it and no other
    /// characteristics" (CR 405.4). It keeps its source's card here only so a client can show
    /// what produced it; nothing in the rules reads those characteristics. When it finishes
    /// resolving it ceases to exist rather than going to a graveyard — it was never a card.
    /// </remarks>
    public AbilityOnStack? Ability { get; init; }

    /// <summary>Convenience for the common check; see <see cref="Permanent"/>.</summary>
    public bool IsPermanent => Permanent is not null;

    /// <remarks>
    /// The card is compared by oracle id rather than by reference. Within one game the same
    /// <see cref="CardDefinition"/> instance travels with the object, but a state rebuilt from a
    /// stored log has its own instances, and two states of the same game must still be equal.
    /// </remarks>
    public bool Equals(GameObject? other) =>
        other is not null &&
        Id == other.Id &&
        OwnerId == other.OwnerId &&
        ControllerId == other.ControllerId &&
        Zone == other.Zone &&
        Timestamp == other.Timestamp &&
        string.Equals(Card.OracleId, other.Card.OracleId, StringComparison.Ordinal) &&
        Equals(Permanent, other.Permanent) &&
        Equals(Ability, other.Ability);

    public override int GetHashCode() =>
        HashCode.Combine(Id, OwnerId, ControllerId, Zone, Timestamp, Card.OracleId, Permanent);
}
