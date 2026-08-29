using MtgEngine.Rules.Abilities;

namespace MtgEngine.Rules.State;

/// <summary>Which damage a prevention effect watches for (CR 615.1).</summary>
/// <remarks>
/// "Prevent all <em>combat</em> damage that would be dealt this turn" is the commonest prevention
/// line in the corpus, and reading it as all damage would turn a fog into a shield against every
/// burn spell on the table. The two words are the whole difference between those cards.
/// </remarks>
public enum DamageKind
{
    /// <summary>Any damage at all.</summary>
    Any,

    /// <summary>Combat damage only (CR 510.1).</summary>
    Combat,

    /// <summary>Damage that is not combat damage.</summary>
    Noncombat,
}

/// <summary>
/// A prevention effect: a shield around a described set of things (CR 615.1).
/// </summary>
/// <remarks>
/// Distinct from <see cref="PermanentState.DamageToPrevent"/> and
/// <see cref="PlayerState.DamageToPrevent"/>, which are CR 615.7's countdown — "prevent the next
/// 3 damage", a fixed pool of points spent as damage arrives. This is the other kind: it names
/// what it shields by description rather than by target, it does not run out, and CR 615.10's
/// numbered form ("prevent 1 of that damage") applies its number afresh to every damage event
/// rather than subtracting from a total.
/// <para>
/// That distinction is why the engine's only prevention was half a feature. A countdown shield
/// is created per target, so nothing untargeted could be shielded at all: "prevent all damage
/// that would be dealt to creatures you control", "…to players", "…by creatures" name no target
/// and had nowhere to go. The existing reader worked around the missing "all" by asking for a
/// shield of one million points, which is a different thing that behaves the same right up until
/// something asks how big the shield is.
/// </para>
/// <para>
/// Every field is data rather than a predicate, because the effect is folded from an event and a
/// delegate cannot be written to a log or compared by value — the same reason
/// <see cref="FloatingEffect"/> holds a definition id instead of an effect.
/// </para>
/// </remarks>
public sealed record PreventionEffect
{
    public required Guid Id { get; init; }

    /// <summary>
    /// Whoever controls the effect, which is who "you" and "you control" are read around.
    /// </summary>
    public required Guid ControllerId { get; init; }

    /// <summary>
    /// The most it prevents from any one damage event, or null for all of it (CR 615.10).
    /// </summary>
    /// <remarks>
    /// Not a countdown. "If a source would deal damage to a Cleric creature you control, prevent
    /// 1 of that damage" prevents one from each damage event however many arrive, which is what
    /// CR 615.10 states and the opposite of what a shield does.
    /// </remarks>
    public int? Amount { get; init; }

    /// <summary>Which damage it watches for.</summary>
    public DamageKind Kind { get; init; }

    /// <summary>One permanent it shields, for the targeted wordings.</summary>
    public ObjectId? Permanent { get; init; }

    /// <summary>
    /// Permanents answering this filter, in the shared vocabulary, or null for none by
    /// description.
    /// </summary>
    public string? PermanentFilter { get; init; }

    /// <summary>Whose permanents <see cref="PermanentFilter"/> counts, or null for anyone's.</summary>
    public PlayerScope? PermanentController { get; init; }

    /// <summary>One player it shields — "dealt to target player".</summary>
    public Guid? Player { get; init; }

    /// <summary>
    /// A described set of players — "dealt to you", "dealt to players".
    /// </summary>
    public PlayerScope? Players { get; init; }

    /// <summary>
    /// A filter the damage's source has to answer — "dealt by creatures" (CR 609.7).
    /// </summary>
    /// <remarks>
    /// The half the existing reader deliberately left unread, and it had to: preventing what a
    /// creature <em>deals</em> is a different question from shielding what reaches it, and a
    /// countdown shield sitting on one permanent cannot ask the first one at all.
    /// </remarks>
    public string? SourceFilter { get; init; }

    /// <summary>Whose sources <see cref="SourceFilter"/> counts, or null for anyone's.</summary>
    public PlayerScope? SourceController { get; init; }

    /// <summary>The turn it ends at the end of, or null for one with no duration.</summary>
    public int? UntilEndOfTurn { get; init; }

    /// <summary>
    /// Whether this effect names nothing to shield, and so shields everything it sees.
    /// </summary>
    /// <remarks>
    /// "Prevent all combat damage that would be dealt this turn" names neither a permanent nor a
    /// player, and means both. Asked here rather than at the two call sites so the answer cannot
    /// differ between damage to a creature and damage to a player.
    /// </remarks>
    public bool ShieldsEverything =>
        Permanent is null && PermanentFilter is null && Player is null && Players is null;
}
