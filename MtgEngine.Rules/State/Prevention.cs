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
    /// One object whose damage it prevents — "dealt by enchanted creature", "dealt by target
    /// creature".
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="Permanent"/>, and a field rather than a
    /// <see cref="SourceFilter"/> because no description of a card can say "that one": the
    /// eleven cards printing this name a single object, and a filter derived from what it
    /// happens to be would shield against every creature of that kind.
    /// <para>
    /// CR 609.7a fixes the source when the effect is created, so this is compared by id and
    /// never looked up. A source that has since left the game answers no rather than answering
    /// everything, which is the fail-closed direction: the alternative reading makes the shield
    /// wider every time its object dies.
    /// </para>
    /// </remarks>
    public ObjectId? Source { get; init; }

    /// <summary>
    /// One permanent <see cref="PermanentFilter"/> does not cover — the word "other".
    /// </summary>
    /// <remarks>
    /// "Prevent all noncombat damage that would be dealt to other creatures you control" is a
    /// filter with a hole in it, and the hole is an identity rather than a description: it is
    /// the permanent whose static ability printed the sentence. Read as the filter alone, Tajic
    /// would shield himself — a better card than the printed one, which is the failure the
    /// prevention family is most dangerous for.
    /// </remarks>
    public ObjectId? Excludes { get; init; }

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
    /// Whether the shield is spent by the first damage it prevents (CR 615.8).
    /// </summary>
    /// <remarks>
    /// "The next time a source of your choice would deal damage to you this turn, prevent that
    /// damage" is a shield with a duration <em>and</em> a use, and it is the use that ends it
    /// first: CR 615.8 stops the next <em>instance</em> from that source however large it is,
    /// and lets every later instance through. A Circle of Protection read without this is a
    /// blanket immunity to a whole source for the turn, which is the direction this family is
    /// most dangerous in — the card looks implemented and plays several times better than the
    /// one on the table.
    /// <para>
    /// It is not <see cref="Amount"/>. That number is CR 615.10's per-event cap, which never runs
    /// out; this is a count of <em>events</em>, and one point from the named source spends the
    /// whole shield exactly as twelve would. The two are independent: no printed card sets both,
    /// but nothing here needs them to be exclusive.
    /// </para>
    /// </remarks>
    public bool OnlyOnce { get; init; }

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

/// <summary>
/// Whether a prevention effect applies to a damage event (CR 615.1).
/// </summary>
/// <remarks>
/// One copy, because the engine keeps two kinds of prevention that mean the same thing. A
/// described prevention a spell resolves is state held in
/// <see cref="GameState.Preventions"/> until the turn ends; the identical words printed as a
/// permanent's static ability are a replacement effect functioning from the battlefield, which
/// stops the moment the permanent does (CR 611.2c). Only the <em>lifetime</em> differs — so
/// "does this shield cover this damage" is asked here by both, rather than twice in two places
/// that would drift about what "creatures you control" means.
/// </remarks>
public static class Preventions
{
    /// <summary>
    /// Whether a prevention effect watches this damage at all — its kind and its source.
    /// </summary>
    /// <remarks>
    /// CR 609.7: "damage from a source" is a question about the object dealing it, so a source
    /// that has left the game answers nothing rather than everything. Preventing damage from a
    /// source that cannot be examined would make "prevent all damage that would be dealt by
    /// creatures" prevent a burn spell too.
    /// </remarks>
    public static bool Watches(
        PreventionEffect effect,
        GameState state,
        IAbilitySource abilities,
        bool isCombat,
        ObjectId sourceId)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(state);

        var kindMatches = effect.Kind switch
        {
            DamageKind.Combat => isCombat,
            DamageKind.Noncombat => !isCombat,
            _ => true,
        };

        if (!kindMatches)
            return false;

        // CR 609.7a: the named source is fixed when the shield is made, so this is an identity
        // and not a description. It is asked before the lookup below because it needs none.
        if (effect.Source is { } named && named != sourceId)
            return false;

        if (effect.SourceFilter is null && effect.SourceController is null)
            return true;

        if (!state.TryGetObject(sourceId, out var source))
            return false;

        if (effect.SourceFilter is { } filter && !SearchFilters.Matches(filter, source.Card))
            return false;

        return effect.SourceController is not { } scope
            || PlayerScopes.Around(scope, state, effect.ControllerId)
                .Contains(Characteristics.ControllerOf(state, abilities, source));
    }

    /// <summary>Whether a prevention effect shields this permanent (CR 615.1).</summary>
    /// <remarks>
    /// The filter is asked of the printed card, as every other card-filter question at this level
    /// is. That is a deviation worth naming: a land animated into a creature is not shielded by
    /// "damage that would be dealt to creatures you control", where CR 613 layer 4 says it should
    /// be. The alternative is a second filter vocabulary over computed characteristics, and the
    /// cards that print this shield name a type the animation cases do not reach.
    /// </remarks>
    public static bool Covers(
        PreventionEffect effect, GameState state, IAbilitySource abilities, GameObject damaged)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(damaged);

        // "Other creatures you control" leaves out the permanent whose ability said it, and no
        // card filter can say that: the exclusion is about which object is speaking rather than
        // about what the card is. Asked first, so that no later arm can shield the one thing the
        // sentence took out — the word only ever narrows.
        if (effect.Excludes == damaged.Id)
            return false;

        if (effect.ShieldsEverything || effect.Permanent == damaged.Id)
            return true;

        if (effect.PermanentFilter is not { } filter
            || !SearchFilters.Matches(filter, damaged.Card))
        {
            return false;
        }

        return effect.PermanentController is not { } scope
            || PlayerScopes.Around(scope, state, effect.ControllerId)
                .Contains(Characteristics.ControllerOf(state, abilities, damaged));
    }

    /// <summary>Whether a prevention effect shields this player (CR 615.1).</summary>
    public static bool CoversPlayer(PreventionEffect effect, GameState state, Guid playerId)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(state);

        return effect.ShieldsEverything
            || effect.Player == playerId
            || (effect.Players is { } scope
                && PlayerScopes.Around(scope, state, effect.ControllerId).Contains(playerId));
    }
}
