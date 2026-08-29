using System.Collections.Immutable;
using MtgEngine.Domain.Enums;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Abilities;

/// <summary>What a target is (CR 115.1).</summary>
public enum TargetKind
{
    Permanent,
    Player,
    SpellOnStack,
    CardInGraveyard,

    /// <summary>
    /// "Any target": a creature, a planeswalker, a battle, or a player (CR 115.4).
    /// </summary>
    /// <remarks>
    /// Its own kind rather than a union of the others, because it is what the cards actually say
    /// and it is the commonest targeting line there is. A spec that could only name one kind
    /// would force every burn spell to be written twice.
    /// </remarks>
    Any,
}

/// <summary>One chosen target (CR 115.1).</summary>
public readonly record struct Target(TargetKind Kind, ObjectId Subject, Guid Player)
{
    public static Target ToPermanent(ObjectId id) => new(TargetKind.Permanent, id, Guid.Empty);

    public static Target ToPlayer(Guid id) => new(TargetKind.Player, default, id);

    public static Target ToSpell(ObjectId id) => new(TargetKind.SpellOnStack, id, Guid.Empty);

    public static Target ToCard(ObjectId id) => new(TargetKind.CardInGraveyard, id, Guid.Empty);
}

/// <summary>
/// What a spell or ability may target (CR 115.1).
/// </summary>
/// <remarks>
/// The legality question is asked twice: when targets are chosen, as the spell is cast
/// (CR 601.2c), and again when it resolves (CR 608.2b). A spell whose only target has become
/// illegal in between does not resolve at all. That second check is why this is a rule the
/// engine keeps rather than something checked once at the point of casting.
/// </remarks>
public sealed record TargetSpec
{
    public required TargetKind Kind { get; init; }

    /// <summary>Reads naturally in an error: "target creature you control".</summary>
    public required string Description { get; init; }

    /// <summary>Which objects qualify. Null accepts any object of the right kind.</summary>
    public Func<GameState, IAbilitySource, GameObject, Guid, bool>? ObjectFilter { get; init; }

    /// <summary>
    /// A further test that also gets to see what is doing the choosing, or null.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ObjectFilter"/> because most phrases do not need it and every
    /// one of them constructs that delegate: "target creature" is the same question whoever
    /// asks. A handful are not - "enchanted creature" means the one this Aura is on, "another
    /// target creature" means not this one, and "creature with lesser power" is lesser than
    /// whose? Those are all the same missing argument.
    /// <para>
    /// A caller that cannot say what its source is skips this test rather than guessing, exactly
    /// as the protection check does.
    /// </para>
    /// </remarks>
    public Func<GameState, IAbilitySource, GameObject, GameObject?, Guid, bool>? SourceFilter
    {
        get;
        init;
    }

    /// <summary>
    /// A further test that gets to see another <em>target of the same effect</em>, or null.
    /// </summary>
    /// <remarks>
    /// The third thing a filter can be about, and the one neither of the others can say. An
    /// <see cref="ObjectFilter"/> sees only the candidate; a <see cref="SourceFilter"/> adds the
    /// permanent whose ability is asking. Neither can answer "each other creature that shares a
    /// color with <em>it</em>" (radiance, 10 cards) or "all other creatures with the same name as
    /// <em>that creature</em>" (Bile Blight and 18 more), because "it" is the creature this same
    /// spell targeted, and no delegate had it.
    /// <para>
    /// The sibling is a target and the group is not. Targets are chosen as the spell is cast
    /// (CR 601.2c) and checked again on resolution (CR 608.2b); the group is found while the
    /// spell resolves and is not targeted at all, so hexproof does not protect it and it does not
    /// fizzle. That asymmetry is the whole shape of these cards and is why this is a filter and
    /// not a second target slot.
    /// </para>
    /// <para>
    /// The peer argument is <em>not</em> nullable, deliberately. A filter that could be handed
    /// null would have to decide what to do about it, and the convenient answer — pass — matches
    /// every permanent on the battlefield, which on these cards is a one-sided board wipe. The
    /// decision is made once, in <see cref="Accepts"/>, and it is to refuse: CR 608.2b says that
    /// if part of an effect requires information about an illegal target it fails to determine
    /// that information, and any part of the effect requiring it does not happen.
    /// </para>
    /// </remarks>
    public Func<GameState, IAbilitySource, GameObject, GameObject, Guid, bool>? PeerFilter
    {
        get;
        init;
    }

    /// <summary>Which players qualify. Null accepts any player still in the game.</summary>
    public Func<GameState, Guid, Guid, bool>? PlayerFilter { get; init; }

    /// <summary>Whether the given target is currently legal for the given controller.</summary>
    /// <summary>
    /// Whether this target may be left unchosen — "up to one target creature" (CR 601.2c).
    /// </summary>
    /// <remarks>
    /// The phrase was being read and then ignored: the engine required a target for every spec it
    /// had, so "up to one" meant "one". That is a real difference — a card with no legal target
    /// could not be cast at all when it should have been castable for none of its effect.
    /// </remarks>
    public bool Optional { get; init; }

    /// <summary>
    /// Whether one object passes every filter this spec carries.
    /// </summary>
    /// <remarks>
    /// The single place that asks all of them, and it exists because forgetting one has been a
    /// live bug twice: two consumers asked <see cref="ObjectFilter"/> and not
    /// <see cref="SourceFilter"/>, so "another creature" offered the source's own name and a
    /// sweeper that said to leave itself out put a counter on itself. A third delegate makes a
    /// hand-written conjunction at each call site three chances to be wrong instead of two, so
    /// there is now one conjunction and every caller uses it.
    /// </remarks>
    /// <param name="source">
    /// The permanent doing the asking, when the caller knows it. Null skips
    /// <see cref="SourceFilter"/> rather than guessing at an answer.
    /// </param>
    /// <param name="peer">
    /// Another target of the same spell or ability, for <see cref="PeerFilter"/>. Null when the
    /// caller has none to offer or when the one it had can no longer be found, and a spec that
    /// carries a peer filter then accepts nothing (CR 608.2b).
    /// </param>
    public bool Accepts(
        GameState state,
        IAbilitySource abilities,
        GameObject candidate,
        Guid controllerId,
        GameObject? source = null,
        GameObject? peer = null)
    {
        if (ObjectFilter?.Invoke(state, abilities, candidate, controllerId) == false)
            return false;

        if (SourceFilter?.Invoke(state, abilities, candidate, source, controllerId) == false)
            return false;

        if (PeerFilter is not { } comparison)
            return true;

        // CR 608.2b: "If part of the effect requires information about an illegal target, it
        // fails to determine any such information. Any part of the effect that requires that
        // information won't happen." Matching everything would be the opposite reading, and on
        // the cards that want this it is the difference between a two-damage ping and a wipe.
        return peer is not null && comparison(state, abilities, candidate, peer, controllerId);
    }

    /// <param name="source">
    /// What is doing the targeting, when it is known. Protection is a question about the source
    /// (CR 702.16b), so a caller that cannot say what the source is gets no protection check
    /// rather than a wrong one.
    /// </param>
    /// <param name="peer">
    /// Another target of the same spell or ability, when the caller has already chosen one
    /// (CR 601.2c). The one printed shape that needs it while <em>choosing</em> is "target
    /// permanent an opponent controls that shares a card type with it", and no caller passes it
    /// yet — so such a spec refuses every target rather than accepting every target, which is a
    /// card that cannot be cast instead of a card that does the wrong thing.
    /// </param>
    public bool IsLegal(
        GameState state,
        IAbilitySource abilities,
        Target target,
        Guid controllerId,
        GameObject? source = null,
        GameObject? peer = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        // A spec that takes any target accepts both shapes; anything else has to match exactly.
        if (Kind == TargetKind.Any)
        {
            if (target.Kind is not (TargetKind.Player or TargetKind.Permanent))
                return false;
        }
        else if (target.Kind != Kind)
        {
            return false;
        }

        if (target.Kind == TargetKind.Player)
        {
            return state.Players.ContainsKey(target.Player)
                && !state.GetPlayer(target.Player).HasLost
                && (PlayerFilter?.Invoke(state, target.Player, controllerId) ?? true);
        }

        if (!state.TryGetObject(target.Subject, out var obj))
            return false;

        var expectedZone = Kind switch
        {
            TargetKind.SpellOnStack => Zone.Stack,
            TargetKind.CardInGraveyard => Zone.Graveyard,
            _ => Zone.Battlefield,
        };

        if (obj.Zone != expectedZone)
            return false;

        // CR 702.11b: hexproof means it cannot be the target of spells or abilities an opponent
        // controls. CR 702.18b: shroud means nobody may target it, including its controller.
        // Both are checked here, where every target passes, rather than at each spell.
        if (Kind is TargetKind.Permanent or TargetKind.Any && obj.Zone == Zone.Battlefield)
        {
            var computed = Characteristics.Of(state, abilities, obj);

            if (computed.Has(KeywordAbility.Shroud))
                return false;

            if (computed.Has(KeywordAbility.Hexproof) && obj.ControllerId != controllerId)
                return false;

            // CR 702.16b: protection from a quality also stops a permanent being targeted by
            // anything with that quality. It is a question about the source rather than about the
            // target, which is why the signature carries one — and why a caller that does not
            // know its source simply skips the check instead of guessing.
            if (source is not null
                && computed.IsProtectedFrom(Characteristics.Of(state, abilities, source)))
            {
                return false;
            }
        }

        return Accepts(state, abilities, obj, controllerId, source, peer);
    }
}

/// <summary>
/// The comparisons a <see cref="TargetSpec.PeerFilter"/> is built from (CR 608.2h).
/// </summary>
/// <remarks>
/// Two questions between them cover every card the corpus prints in this shape, measured rather
/// than guessed: "shares a color with it" is 10 cards, all of them radiance, and "with the same
/// name as that [noun]" is 19 across creatures, permanents, lands, artifacts and enchantments.
/// They are kept apart from the "other" exclusion because that is a third question — the printed
/// line says "each <em>other</em>", and a comparison that hid the exclusion inside itself could
/// not be reused by anything that does not.
/// <para>
/// Each is handed the object it is judging and the sibling to judge it against, and asks nothing
/// about where either one is. That is what lets the sibling be a card the same resolution has
/// already exiled: CR 608.2h says an effect needing information about an object that has left the
/// zone it was expected to be in uses its last known information, and Sever the Bloodline exiles
/// its target before the group it describes is gathered.
/// </para>
/// </remarks>
public static class PeerFilters
{
    /// <summary>"…that shares a color with it" (CR 105.2).</summary>
    /// <remarks>
    /// Colour is read from the computed characteristics on both sides, because layer 5 changes it
    /// (CR 613.1e) and a creature painted white by an effect shares a colour with a white spell's
    /// target. The one thing this does not reproduce is last known <em>colour</em>: a sibling that
    /// has left the battlefield is asked where it is now, so a layer-5 effect that was on it there
    /// is gone. No printed card reaches that — the only mid-resolution sibling in the corpus is
    /// compared by name, and a colour comparison whose target has left fizzles first (CR 608.2b).
    /// </remarks>
    public static bool SharesAColour(
        GameState state,
        IAbilitySource abilities,
        GameObject candidate,
        GameObject peer,
        Guid controllerId)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(peer);

        var theirs = Characteristics.Of(state, abilities, peer).Colors;

        // CR 105.2c: a colourless object has no colour, so it shares one with nothing — not even
        // with another colourless object. An empty intersection says that without a special case.
        return theirs.Count != 0
            && Characteristics.Of(state, abilities, candidate).Colors.Any(theirs.Contains);
    }

    /// <summary>"…with the same name as that creature" (CR 201.2a).</summary>
    /// <remarks>
    /// The name is read off the card rather than off the computed characteristics because nothing
    /// in this engine changes a name — face-down is the one thing that does, and CR 707.2 makes a
    /// face-down permanent nameless, which is exactly what the second half of CR 201.2a is about.
    /// </remarks>
    public static bool HasTheSameName(
        GameState state,
        IAbilitySource abilities,
        GameObject candidate,
        GameObject peer,
        Guid controllerId)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(peer);

        // CR 707.2: a face-down permanent has no name.
        if (candidate.Permanent is { IsFaceDown: true } || peer.Permanent is { IsFaceDown: true })
            return false;

        // CR 201.2a: "An object with no name doesn't have the same name as any other object,
        // including another object with no name." Two nameless tokens are not each other's twin,
        // and string equality alone would have said they were.
        return !string.IsNullOrEmpty(candidate.Card.Name)
            && string.Equals(candidate.Card.Name, peer.Card.Name, StringComparison.Ordinal);
    }

    /// <summary>The same comparison with the sibling itself left out — "each other …".</summary>
    /// <remarks>
    /// Every one of the 29 cards measured for this says "other", because each pairs the group
    /// with a targeted effect on the sibling and would otherwise hit it twice. It is a wrapper
    /// rather than a flag on the comparison so that the two questions stay separable: a card that
    /// said "each creature with the same name" would want one and not the other.
    /// </remarks>
    public static Func<GameState, IAbilitySource, GameObject, GameObject, Guid, bool> Other(
        Func<GameState, IAbilitySource, GameObject, GameObject, Guid, bool> comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        return (state, abilities, candidate, peer, controller) =>
            candidate.Id != peer.Id && comparison(state, abilities, candidate, peer, controller);
    }
}

/// <summary>
/// How much of something an effect does: a printed number, or X (CR 601.2b).
/// </summary>
/// <remarks>
/// The value chosen for X has been carried from the cast all the way to
/// <see cref="ResolutionContext.VariableValue"/> since the engine was built, and no effect ever
/// read it — so "deals X damage" and "draw X cards" compiled to nothing and every X spell in the
/// game was unplayable.
/// <para>
/// It converts implicitly from <see langword="int"/> so that the hundred existing call sites that
/// pass a plain number keep reading as they did. An effect asks <see cref="In"/> for the number
/// rather than storing one, because X is not known until the spell is cast and the same
/// definition is shared by every casting of that card.
/// </para>
/// </remarks>
public readonly record struct Amount(int Fixed, bool IsVariable = false)
{
    /// <summary>The value the caster chose for X (CR 601.2b).</summary>
    public static readonly Amount X = new(0, IsVariable: true);

    public static implicit operator Amount(int value) => new(value);

    /// <summary>Whether this amount is counted the other way — how life loss is written.</summary>
    /// <remarks>
    /// A flag rather than a negative <see cref="Fixed"/>, because negating the fixed part only
    /// works when the fixed part <em>is</em> the amount. It is not when X was chosen by the caster
    /// and it is not when the amount is a count: X's fixed part is zero, so negating it produced
    /// zero and left the variable flag alone, and "each opponent loses X life" resolved to each
    /// opponent <em>gaining</em> X life. Nothing failed and no line went unread.
    /// </remarks>
    public bool Negated { get; init; }

    /// <summary>The same amount, counted the other way — how life loss is written.</summary>
    public static Amount operator -(Amount amount) => amount with { Negated = !amount.Negated };

    /// <summary>Named alternative to the negation operator, for callers that want words.</summary>
    public static Amount Negate(Amount amount) => -amount;

    /// <summary>
    /// What to multiply by, when the amount is "for each" something (CR 107.3).
    /// </summary>
    /// <remarks>
    /// "Gain 2 life for each creature you control" is two per creature, and "draw cards equal to
    /// the number of Islands you control" is one per Island — the same shape with the multiplier
    /// left implicit. Both are this: a fixed part times a count taken when the effect resolves.
    /// <para>
    /// It lives on the amount rather than on each effect because "for each" attaches to numbers
    /// generally, not to any one thing a card does. Every effect that already takes an Amount can
    /// be counted this way without knowing about it.
    /// </para>
    /// </remarks>
    public Func<ResolutionContext, int>? Counter { get; init; }

    /// <summary>The number this comes to for the spell or ability now resolving.</summary>
    public int In(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var size = Counter is { } counted
            ? Fixed * counted(context)
            : IsVariable ? context.VariableValue : Fixed;

        return Negated ? -size : size;
    }

    public override string ToString() =>
        Counter is not null ? $"{Fixed} for each"
            : IsVariable ? "X"
            : Fixed.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Everything an effect needs to know while it resolves (CR 608.2).</summary>
public sealed record ResolutionContext
{
    public required GameState State { get; init; }

    public required IAbilitySource Abilities { get; init; }

    /// <summary>Who controls the spell or ability, and so who "you" means (CR 608.2).</summary>
    public required Guid ControllerId { get; init; }

    /// <summary>The object on the stack that is resolving.</summary>
    public required ObjectId SourceId { get; init; }

    public ImmutableList<Target> Targets { get; init; } = [];

    /// <summary>
    /// The player the triggering event was about, for <see cref="PlayerScope.TriggerSubject"/>.
    /// </summary>
    /// <remarks>
    /// Null for a spell and for an activated ability, because neither has one — nothing about
    /// casting a spell picks out a player the way "whenever this deals damage to a player" does.
    /// </remarks>
    public Guid? SubjectPlayer { get; init; }

    /// <summary>The object the triggering event was about, for a trigger that says "it".</summary>
    public ObjectId? SubjectObject { get; init; }

    /// <summary>How much the triggering event was about — "that many" (CR 603.2).</summary>
    public int? SubjectAmount { get; init; }

    /// <summary>The value chosen for X as the spell was cast (CR 601.2b).</summary>
    public int VariableValue { get; init; }

    /// <summary>How much each target is to be dealt, by target index (CR 601.2d).</summary>
    public ImmutableList<int> DamageDivision { get; init; } = [];

    /// <summary>
    /// Who controlled an object, whether or not that object still exists (CR 400.7).
    /// </summary>
    /// <remarks>
    /// Effects in one resolution each see what the previous one left behind (CR 608.2c), so a
    /// second sentence that says "its controller" is asking about something the first sentence
    /// has already destroyed, countered or exiled — which made it a different object under a new
    /// id, and <see cref="GameState.TryGetObject"/> finds nothing. This follows the id forward
    /// through the log the way the engine's own deferred questions do.
    /// <para>
    /// Null when nothing was ever known about the id, and null on a context built without one —
    /// callers fall back rather than assume, because an unanswerable question is not a broken
    /// game.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Which of the source's abilities is resolving, when one is (CR 405.4).
    /// </summary>
    /// <remarks>
    /// Carried here rather than looked up from the state, because the two disagree exactly when
    /// it matters. An effect that has to be found again later - one that asks a question and is
    /// located by (source, ability, index) when the answer arrives - runs from a *deferred*
    /// branch after the ability that raised it has left the stack. Asking the state then gives
    /// the permanent, which has no ability on it, so the locator searched the card's spell
    /// effects instead of the ability's, found nothing, and the request was dropped in silence:
    /// no question, no error, the branch simply did not happen.
    /// </remarks>
    public string? AbilityId { get; init; }

    public Func<ObjectId, Guid?>? ControllerBehind { get; init; }

    /// <summary>
    /// The object an id became, whether or not that id still names anything (CR 400.7).
    /// </summary>
    /// <remarks>
    /// The twin of <see cref="ControllerBehind"/>, for effects that need the object rather than
    /// who controlled it. A death trigger's source is the permanent that died, and by the time
    /// the trigger resolves that permanent is a card in a graveyard under a new id - so an effect
    /// that wants to move it has to be able to follow the id forward.
    /// </remarks>
    public Func<ObjectId, GameObject?>? ObjectBehind { get; init; }

    public Target? TargetAt(int index) =>
        index >= 0 && index < Targets.Count ? Targets[index] : null;

    /// <summary>
    /// The object a target of this same spell or ability names, wherever it has got to
    /// (CR 608.2h).
    /// </summary>
    /// <remarks>
    /// What "it" and "that creature" mean to a group filter in the same sentence — "each other
    /// creature that shares a color with <em>it</em>". <see cref="TargetAt"/> answers with the
    /// chosen target; this answers with the object, and it has to keep answering after an earlier
    /// effect of the same resolution has moved it. Sever the Bloodline exiles its target and then
    /// describes a group by that target's name, and a permanent that leaves the battlefield does
    /// so under a new id (CR 400.7), so asking the state alone returns nothing on exactly the card
    /// the mechanism exists for.
    /// <para>
    /// Null when the target names a player, when the id cannot be followed, and on a context built
    /// without <see cref="ObjectBehind"/>. Callers must treat null as "cannot be determined" and
    /// do nothing (CR 608.2b) rather than as "no restriction".
    /// </para>
    /// </remarks>
    public GameObject? PeerAt(int index)
    {
        if (TargetAt(index) is not { } target || target.Kind == TargetKind.Player)
            return null;

        return State.TryGetObject(target.Subject, out var live)
            ? live
            : ObjectBehind?.Invoke(target.Subject);
    }

    /// <summary>
    /// The object that is the <em>source</em> of what this effect does (CR 608.2, 609.7).
    /// </summary>
    /// <remarks>
    /// Not the same as <see cref="SourceId"/>. What resolves is a spell or an ability, and an
    /// ability on the stack is its own object — but the source of damage an ability deals is the
    /// permanent whose ability it is, not the ability. The difference is invisible until something
    /// asks about the source: lifelink on the creature, deathtouch on the creature, and every
    /// "whenever this creature deals damage" trigger all read this and would all miss.
    /// <para>
    /// For a spell the two are the same object, which is why the fallback is <see cref="SourceId"/>
    /// rather than a failure.
    /// </para>
    /// </remarks>
    public ObjectId PhysicalSourceId =>
        State.TryGetObject(SourceId, out var onStack) && onStack.Ability is { } ability
            ? ability.SourceId
            : SourceId;
}

/// <summary>
/// One thing a spell or ability does when it resolves.
/// </summary>
/// <remarks>
/// The vocabulary cards are built from. An effect reports the events it wants to happen; it does
/// not apply them, so it cannot mutate state behind the reducer's back and everything it does
/// lands in the log like everything else.
/// </remarks>
public interface IEffect
{
    IReadOnlyList<GameEvent> Resolve(ResolutionContext context);
}

/// <summary>Deals damage to a target creature or player (CR 119.3, 120).</summary>
public sealed record DealDamage(Amount Amount, int TargetIndex = 0, bool Deathtouch = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { } target)
            return [];

        var source = context.PhysicalSourceId;

        return target.Kind switch
        {
            TargetKind.Player =>
                [new PlayerDamaged(target.Player, source, Amount.In(context), IsCombat: false)],
            // CR 120.1: damage is dealt to a permanent, and a permanent is on the battlefield.
            // A creature that died or was bounced in response is an illegal target and is skipped
            // (CR 608.2b) - the engine's own damage record refuses anything else, so this has to
            // be asked here rather than discovered there.
            TargetKind.Permanent =>
                context.State.TryGetObject(target.Subject, out var hit)
                && hit.Zone == Zone.Battlefield
                    ? [new DamageMarked(target.Subject, Amount.In(context), Deathtouch, source)]
                    : [],
            _ => [],
        };
    }
}

/// <summary>Destroys a target permanent (CR 701.8).</summary>
/// <remarks>
/// Destruction is a move to the graveyard, which indestructible replaces and regeneration can
/// replace (CR 701.8c). It goes through the same event as any other zone change, so those
/// replacements see it.
/// </remarks>
public sealed record DestroyTarget(
    int TargetIndex = 0,
    bool NoRegeneration = false,
    EffectSubject Subject = EffectSubject.Target) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } victim)
            return [];

        if (!context.State.TryGetObject(victim, out var permanent))
            return [];

        // CR 701.7a: destroy applies to a permanent, and a permanent is on the battlefield.
        // A pronoun can now name an object in a graveyard - "whenever a creature dies, destroy
        // that creature" is a card nobody prints, but the grammar allows the sentence - and
        // destroying something that has already left does nothing rather than moving it again.
        if (permanent.Zone != Zone.Battlefield)
            return [];

        // CR 702.12b: a permanent with indestructible is not destroyed by an effect that says
        // "destroy". The spell still resolves and the target was legal — nothing happens to it.
        // The state-based actions already knew this about lethal damage; a Murder did not, so
        // it killed a Darksteel creature outright.
        if (Characteristics.HasKeyword(
                context.State, context.Abilities, permanent, KeywordAbility.Indestructible))
        {
            return [];
        }

        return
        [
            new ObjectMoved(
                victim,
                ObjectId.New(),
                Zone.Battlefield,
                Zone.Graveyard,
                permanent.ControllerId,
                NoRegeneration ? MoveCause.DestroyNoRegeneration : MoveCause.Destroy),
        ];
    }
}

/// <summary>Exiles a target permanent (CR 406.2).</summary>
public sealed record ExileTarget(
    int TargetIndex = 0, EffectSubject Subject = EffectSubject.Target) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } exiled)
            return [];

        if (!context.State.TryGetObject(exiled, out var permanent))
            return [];

        // The zone it is actually in, not the battlefield. Exile reaches other zones and always
        // did on the cards - "exile that creature" on a dies trigger means the card now in the
        // graveyard (CR 400.7) - but this had the origin hardcoded, so the move described a
        // journey the object was not on and the reducer refused it outright. It was invisible
        // while every pronoun this effect could see pointed at the battlefield.
        return
        [
            new ObjectMoved(
                exiled, ObjectId.New(), permanent.Zone, Zone.Exile,
                permanent.ControllerId, MoveCause.Exile),
        ];
    }
}

/// <summary>
/// Exiles a permanent and returns it to the battlefield at once — a flicker (CR 400.7).
/// </summary>
/// <remarks>
/// One effect and not two, because the card that comes back is a <em>different object</em> and
/// nothing could name it in between: an exile followed by a separate return would have to find a
/// card by an id that stopped existing the moment it was exiled.
/// <para>
/// What comes back has no counters, no Auras, no damage and none of the abilities anything gave
/// it, and it is summoning-sick again. None of that is arranged here — it is simply what changing
/// zones means, and it is the whole reason these cards are played: the permanent enters, so
/// everything that triggers on entering triggers again.
/// </para>
/// <para>
/// "Under its owner's control" is the ordinary case and what a new object gets anyway. The cards
/// that say "under your control" are a theft, and they are not read here — reading them as this
/// would quietly hand the permanent back to the player it was taken from.
/// </para>
/// </remarks>
/// <summary>
/// Exiles the permanent this ability belongs to and returns it at once (CR 400.7).
/// </summary>
/// <remarks>
/// <see cref="FlickerTarget"/> written about the source instead of a target - and the difference
/// is not only which object: a card that blinks *itself* is usually doing it to arrive as its
/// other face, which is what <paramref name="Transformed" /> is for.
/// <para>
/// What comes back is a new object (CR 400.7), so nothing it had before travels with it - no
/// counters, no auras, no damage. That is the point of every card printed this way.
/// </para>
/// </remarks>
public sealed record FlickerSource(bool Transformed = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        if (!context.State.TryGetObject(subject, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        var exiled = ObjectId.New();
        var returning = ObjectId.New();

        var events = new List<GameEvent>
        {
            new ObjectMoved(
                subject, exiled, Zone.Battlefield, Zone.Exile,
                permanent.OwnerId, MoveCause.Exile),
            new ObjectMoved(
                exiled, returning, Zone.Exile, Zone.Battlefield,
                permanent.OwnerId, MoveCause.Return),
        };

        // A card with nothing to turn over folds this to nothing, which is the right answer for
        // a sentence that cannot apply rather than a reason to refuse the line.
        if (Transformed)
            events.Add(new PermanentTransformed(returning, 1));

        return events;
    }
}

/// <summary>
/// Exiles a permanent and brings it back at the next end step (CR 603.7b).
/// </summary>
/// <remarks>
/// The slow flicker. Its immediate twin below does both moves at once; this one has to name the
/// exiled card afterwards, and a card that changes zones is a new object (CR 400.7) - so the
/// delayed trigger is created here, where the id the exile produced is still in hand.
/// <para>
/// The trigger is filed under the card's owner, because it returns "under its owner's control"
/// and the dispatch moves it for whoever the trigger belongs to.
/// </para>
/// </remarks>
public sealed record ExileAndReturnAtEndStep(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target
            || !context.State.TryGetObject(target.Subject, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        var exiled = ObjectId.New();

        return
        [
            new ObjectMoved(
                target.Subject, exiled, Zone.Battlefield, Zone.Exile,
                permanent.OwnerId, MoveCause.Exile),

            new DelayedTriggerCreated(
                Guid.NewGuid(),
                permanent.OwnerId,
                exiled,
                State.TurnStep.End,
                "return-to-battlefield",
                context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Runs an exile and brings back whatever it exiled at the beginning of the next end step.
/// </summary>
/// <remarks>
/// A wrapper rather than a variant of each exile, because the sentence before this one takes
/// many shapes - a target, a group, the source itself - and every one of them ends with the same
/// clause. It reads its own inner effect's events to learn which object was exiled, which is the
/// only place that answer exists: the card that left the battlefield is a new object now
/// (CR 400.7) and nothing else in the resolution knows its new id.
/// </remarks>
public sealed record ReturnAtNextEndStep(IEffect Inner) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var produced = Inner.Resolve(context);
        var armed = new List<GameEvent>(produced);

        foreach (var moved in produced.OfType<ObjectMoved>())
        {
            if (moved.To != Zone.Exile)
                continue;

            armed.Add(new DelayedTriggerCreated(
                Guid.NewGuid(),
                moved.ControllerId,
                moved.NewId,
                State.TurnStep.End,
                "return-to-battlefield",
                context.State.TurnNumber));
        }

        return armed;
    }
}

public sealed record FlickerTarget(int TargetIndex = 0, bool Tapped = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target
            || !context.State.TryGetObject(target.Subject, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        var exiled = ObjectId.New();
        var returning = ObjectId.New();

        var events = new List<GameEvent>
        {
            new ObjectMoved(
                target.Subject, exiled, Zone.Battlefield, Zone.Exile,
                permanent.OwnerId, MoveCause.Exile),
            new ObjectMoved(
                exiled, returning, Zone.Exile, Zone.Battlefield,
                permanent.OwnerId, MoveCause.Return),
        };

        if (Tapped)
            events.Add(new PermanentTapped(returning));

        return events;
    }
}

/// <summary>Draws cards (CR 121.3). "You" is the controller unless a target says otherwise.</summary>
/// <summary>
/// Returns a target permanent to its owner's hand (CR 400.3).
/// </summary>
/// <remarks>
/// Its <em>owner's</em> hand, not its controller's. The two are the same in almost every game
/// and different in exactly the ones that matter — a creature you have taken control of goes
/// home when it is bounced, and a rule written against the controller would quietly steal it.
/// </remarks>
public sealed record ReturnToHand(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        if (!context.State.TryGetObject(target.Subject, out var permanent))
            return [];

        // CR 608.2b: a target that has stopped being legal is skipped, and the spell does as much
        // as it can with the rest. A creature bounced or killed in response to this is exactly
        // that, and describing a move out of the battlefield it has already left was refused by
        // the reducer rather than quietly doing nothing.
        if (permanent.Zone != Zone.Battlefield)
            return [];

        return
        [
            new ObjectMoved(
                target.Subject, ObjectId.New(), Zone.Battlefield, Zone.Hand,
                permanent.OwnerId, MoveCause.Return),
        ];
    }
}

/// <summary>
/// Puts a target permanent into its owner's library (CR 400.7).
/// </summary>
/// <remarks>
/// A harder bounce than <see cref="ReturnToHand"/>, and the difference is the whole reason cards
/// print it: the permanent is gone for at least a draw, and on the bottom it is gone for good.
/// Which end it goes to is the entire point, so it is a parameter rather than a default.
/// </remarks>
public sealed record PutTargetOnLibrary(
    int TargetIndex = 0, ZonePosition Position = ZonePosition.Top) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A permanent on the battlefield and a card in a graveyard are both put onto a library
        // by this line, and the zone the card leaves has to be the one it is actually in: naming
        // the battlefield unconditionally meant a graveyard target moved nowhere at all.
        if (context.TargetAt(TargetIndex) is not
            { Kind: TargetKind.Permanent or TargetKind.CardInGraveyard } target)
        {
            return [];
        }

        if (!context.State.TryGetObject(target.Subject, out var card)
            || card.Zone is not (Zone.Battlefield or Zone.Graveyard))
        {
            return [];
        }

        return
        [
            new ObjectMoved(
                target.Subject,
                ObjectId.New(),
                card.Zone,
                Zone.Library,
                card.OwnerId,
                MoveCause.Return,
                Position),
        ];
    }
}

/// <summary>
/// Shows a player's hand to everybody (CR 701.16a).
/// </summary>
/// <remarks>
/// Whose hand is a target when the card names one and a scope when it names a group, which is
/// why both are here: "target opponent reveals their hand" and "each opponent reveals their
/// hand" are the same act asked of different people.
/// </remarks>
/// <summary>"Look at target opponent's hand" (CR 701.19a).</summary>
public sealed record LookAtHand(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Player } target
            || !context.State.Players.ContainsKey(target.Player))
        {
            return [];
        }

        var hand = context.State.GetPlayer(target.Player).Hand;

        return hand.IsEmpty
            ? []
            : [new HandLookedAt(context.ControllerId, target.Player, hand)];
    }
}

public sealed record RevealHand(int? TargetIndex = null, PlayerScope Scope = PlayerScope.You)
    : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IEnumerable<Guid> who = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Player } target ? [target.Player] : []
            : PlayerScopes.Resolve(Scope, context);

        return
        [
            .. who
                .Select(id => (Player: id, Hand: context.State.GetPlayer(id).Hand))
                .Where(shown => !shown.Hand.IsEmpty)
                .Select(shown => new CardsRevealed(shown.Player, shown.Hand)),
        ];
    }
}

/// <summary>
/// A target permanent sits out its controller's next untap step (CR 502.3).
/// </summary>
/// <remarks>
/// The tail of "tap target creature. That creature doesn't untap during its controller's next
/// untap step", which is why it names no target of its own - it reads the one the sentence
/// before it chose.
/// </remarks>
public sealed record SkipNextUntap(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target
            || !context.State.TryGetObject(target.Subject, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        return [new UntapSkipped(target.Subject, Skipping: true)];
    }
}

/// <summary>
/// The source itself skips its controller's next untap step (CR 502.3).
/// </summary>
/// <remarks>
/// The tail of a mana ability far more often than a spell: "{T}: Add {C}{C}. This land doesn't
/// untap during your next untap step" is a land that pays double and then sits out a turn. It
/// names no target because there is nothing to aim at - the permanent that produced the ability
/// is the one that stays tapped, and asking would offer a choice the card does not give.
/// </remarks>
public sealed record SkipNextUntapSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        // Gone from the battlefield before the ability resolved: nothing to keep tapped, and an
        // event naming an object that is not there would replay into a state that has no room
        // for it.
        if (!context.State.TryGetObject(subject, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        return [new UntapSkipped(subject, Skipping: true)];
    }
}

/// <summary>
/// Deals damage split among the targets in the amounts announced as the spell was cast
/// (CR 601.2d).
/// </summary>
/// <remarks>
/// The division is not made here. It was made as the spell was cast, is public from that moment,
/// and rides on the stack object — so this effect only spends what is already decided. An effect
/// that asked on resolution would be a different card: an opponent who let the spell resolve did
/// so knowing exactly where the damage was going.
/// <para>
/// A target that has become illegal is skipped and its share is simply not dealt. The damage is
/// not moved to the others, because the division named that target and CR 608.2b does not
/// redistribute what a lost target was owed.
/// </para>
/// </remarks>
public sealed record DealDividedDamage(int Total, int TargetCount, int FirstIndex = 0)
    : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        for (var i = 0; i < TargetCount; i++)
        {
            var slot = FirstIndex + i;
            if (slot >= context.DamageDivision.Count)
                break;

            var amount = context.DamageDivision[slot];
            if (amount <= 0)
                continue;

            events.AddRange(new DealDamage(new Amount(amount), slot).Resolve(context));
        }

        return events;
    }
}

/// <summary>Taps a target permanent (CR 701.26a).</summary>
/// <remarks>
/// Tapping something already tapped does nothing at all — it is not an error and the spell is
/// not countered, so this returns no event rather than emitting one that would read as a second
/// tap in the log.
/// </remarks>
/// <summary>
/// "Return that card to the battlefield under your control" - reanimating what the trigger was
/// about (CR 400.7).
/// </summary>
/// <remarks>
/// The subject is a creature that died, so the id the trigger carries names the permanent it was
/// and not the card it has become. Followed forward to the card, which is the only thing that can
/// be returned - the permanent stopped existing the moment it left.
/// </remarks>
public sealed record ReturnSubjectCardToBattlefield(bool UnderYourControl = true) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.SubjectObject is not { } was)
            return [];

        var card = context.State.TryGetObject(was, out var present)
            ? present
            : context.ObjectBehind?.Invoke(was);

        // Only from a graveyard: something that has since been exiled or shuffled away is gone,
        // and a card that has moved on is not "that card" any more.
        if (card is not { Zone: Zone.Graveyard })
            return [];

        var newId = ObjectId.New();

        return
        [
            new ObjectMoved(
                card.Id,
                newId,
                Zone.Graveyard,
                Zone.Battlefield,
                UnderYourControl ? context.ControllerId : card.OwnerId,
                MoveCause.Other),
        ];
    }
}

/// <summary>
/// Taps something and keeps it down through its controller's next untap step (CR 302.6).
/// </summary>
/// <remarks>
/// One effect for two printed clauses, because the second names what the first just tapped and
/// an effect never sees the events of the one before it. The freeze is emitted even when the
/// permanent was already tapped: "tap that creature and it doesn't untap" is two instructions,
/// and the second does not depend on the first having changed anything.
/// </remarks>
public sealed record TapAndFreeze(
    int TargetIndex = 0, EffectSubject Subject = EffectSubject.Target) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } frozen
            || !context.State.TryGetObject(frozen, out var caught)
            || caught.Zone != Zone.Battlefield)
        {
            return [];
        }

        return caught.Permanent is { IsTapped: true }
            ? [new UntapSkipped(frozen, true)]
            : [new PermanentTapped(frozen), new UntapSkipped(frozen, true)];
    }
}

public sealed record TapTarget(
    int TargetIndex = 0, EffectSubject Subject = EffectSubject.Target) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } tapping)
            return [];

        if (!context.State.TryGetObject(tapping, out var permanent))
            return [];

        if (permanent.Permanent?.IsTapped != false)
            return [];

        return [new PermanentTapped(tapping)];
    }
}

/// <summary>Untaps a target permanent (CR 701.26b).</summary>
/// <remarks>
/// Reuses the untap step's event rather than adding a singular one. There is no such thing as
/// untapping "one at a time" in the rules — the untap step untaps a whole set simultaneously —
/// and a second event meaning the same thing is a second thing every reducer and replay has to
/// know about.
/// </remarks>
public sealed record UntapTarget(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        if (!context.State.TryGetObject(target.Subject, out var permanent))
            return [];

        if (permanent.Permanent?.IsTapped != true)
            return [];

        return [new PermanentsUntapped([target.Subject])];
    }
}

public sealed record DrawCards(
    Amount Count,
    int? TargetIndex = null,
    PlayerScope Scope = PlayerScope.You) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A named target wins outright over a scope, the same way it does for a discard: the
        // sentence named one player and the scope named none.
        if (TargetIndex is { } index)
        {
            var aimed = context.TargetAt(index)?.Player ?? context.ControllerId;
            return Drawing.From(context, aimed, Count.In(context));
        }

        var drawn = new List<GameEvent>();
        var count = Count.In(context);

        foreach (var who in PlayerScopes.Resolve(Scope, context))
            drawn.AddRange(Drawing.From(context, who, count));

        return drawn;
    }
}

/// <summary>Gains or loses life (CR 119.3).</summary>
public sealed record ChangeLife(Amount Amount, int? TargetIndex = null) : IEffect
{
    /// <summary>Whose life changes, when the sentence names a group rather than a target.</summary>
    public PlayerScope Scope { get; init; } = PlayerScope.You;

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A named target wins outright over a scope, the same way it does for drawing and
        // discarding: the sentence named one player and the scope named none.
        var amount = Amount.In(context);

        if (TargetIndex is { } index)
        {
            var aimed = context.TargetAt(index)?.Player ?? context.ControllerId;
            return [new LifeChanged(aimed, amount, context.State.GetPlayer(aimed).Life + amount)];
        }

        return
        [
            .. PlayerScopes.Resolve(Scope, context).Select(who =>
                new LifeChanged(who, amount, context.State.GetPlayer(who).Life + amount)),
        ];
    }
}

/// <summary>
/// Changes the life total of whoever controls a target (CR 608.2).
/// </summary>
/// <remarks>
/// The tail of a counterspell: "Counter target creature spell. Its controller loses 1 life."
/// "Its" is the target the sentence before chose, so this reads a target slot like any other
/// effect — what is different is that it wants the target's <em>controller</em> rather than the
/// target itself.
/// <para>
/// A target that is already a player is used directly, so the same effect reads "target player
/// loses 2 life" if a card ever phrases it that way.
/// </para>
/// </remarks>
public sealed record ChangeLifeOfTargetsController(Amount Amount, int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (TargetOwnership.ControllerOf(context, TargetIndex) is not { } who)
            return [];

        var amount = Amount.In(context);
        return [new LifeChanged(who, amount, context.State.GetPlayer(who).Life + amount)];
    }
}

/// <summary>
/// The controller of a target draws cards — "its controller draws a card" (CR 121.3).
/// </summary>
/// <remarks>
/// The same player <see cref="ChangeLifeOfTargetsController"/> finds, and found the same way,
/// including the case that makes it awkward: the target may have been countered or destroyed a
/// sentence ago and be a card in a graveyard under a new id (CR 400.7). Whose it was is still a
/// fact about the game, so the controller is followed back rather than given up on.
/// </remarks>
public sealed record DrawForTargetsController(Amount Count, int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return TargetOwnership.ControllerOf(context, TargetIndex) is { } who
            ? Drawing.From(context, who, Count.In(context))
            : [];
    }
}

/// <summary>Which player a target belongs to (CR 608.2).</summary>
/// <remarks>
/// One question asked by more than one effect — "its controller loses 2 life", "its controller
/// draws a card" — and worth a name of its own because the awkward part is shared too: the target
/// may have been countered or destroyed a sentence ago and be a card in a graveyard under a new
/// id (CR 400.7). Whose it was is still a fact about the game, so the controller is followed back
/// rather than given up on. Written twice, the second copy is where that arm gets forgotten.
/// </remarks>
public static class TargetOwnership
{
    public static Guid? ControllerOf(ResolutionContext context, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(targetIndex) is not { } target)
            return null;

        if (target.Kind == TargetKind.Player)
            return target.Player;

        if (context.State.TryGetObject(target.Subject, out var owner))
            return owner.ControllerId;

        return context.ControllerBehind?.Invoke(target.Subject);
    }
}

/// <summary>Drawing cards, and what happens when there are none (CR 121.3, 121.4).</summary>
/// <remarks>
/// Shared because the empty-library arm is the part that matters and the part a second copy
/// would omit: the draw does not happen, the attempt is remembered, and the player loses to a
/// state-based action later rather than here — so the rest of the effect still resolves.
/// </remarks>
public static class Drawing
{
    public static IReadOnlyList<GameEvent> From(ResolutionContext context, Guid who, int count)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();
        var library = context.State.GetPlayer(who).Library;

        for (var i = 0; i < count; i++)
        {
            if (i >= library.Count)
            {
                events.Add(new DrawFromEmptyLibraryAttempted(who));
                break;
            }

            events.Add(new ObjectMoved(
                library[i], ObjectId.New(), Zone.Library, Zone.Hand, who, MoveCause.Draw));
        }

        return events;
    }
}

/// <summary>
/// Gives poison counters to the players a scope names (CR 122.1, 704.5c).
/// </summary>
/// <remarks>
/// Poison lives on the player rather than on a permanent, so it takes a scope where the other
/// counter effect takes a target. Ten of them and that player loses (CR 704.5c), which the
/// state-based actions already check.
/// </remarks>
public sealed record GivePoisonCounters(Amount Count, PlayerScope Scope = PlayerScope.You) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var count = Count.In(context);
        return
        [
            .. PlayerScopes.Resolve(Scope, context)
                .Select(who => new PoisonCountersChanged(who, count)),
        ];
    }
}

/// <summary>
/// Counters the spell or ability the trigger was about (CR 701.5a).
/// </summary>
/// <remarks>
/// Ward's "counter it". It cannot be a <see cref="CounterTargetSpell"/> because a ward trigger
/// does not target — that is the point of ward, which would otherwise be stopped by the very
/// hexproof it sits beside. So it counters the trigger's subject instead.
/// <para>
/// A subject that has already left the stack is not an error: something else countered the spell
/// first, or it resolved while the trigger was still waiting. Countering nothing is what the rules
/// do there (CR 701.5b).
/// </para>
/// </remarks>
public sealed record CounterSubjectSpell : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.SubjectObject is not { } subject
            || !context.State.TryGetObject(subject, out var spell)
            || spell.Zone != Zone.Stack)
        {
            return [];
        }

        // CR 701.6a: a spell that can't be countered simply isn't.
        if (context.Abilities.GrantedKeywords(spell.Card).HasFlag(KeywordAbility.CantBeCountered))
            return [];

        return
        [
            new ObjectMoved(
                subject, ObjectId.New(), Zone.Stack, Zone.Graveyard,
                spell.ControllerId, MoveCause.Other),
        ];
    }
}

/// <summary>
/// Creates a token and attaches the source to it (CR 702.90b).
/// </summary>
/// <remarks>
/// Living weapon, and the one shape that cannot be spelled as "create a token" followed by
/// "attach this to it": the second half needs the id of the thing the first half made, and an
/// effect only ever sees the events it returns itself. Two effects in sequence would each be
/// handed a context, and neither context can name a token that did not exist when it was built.
/// <para>
/// The id is generated here and used twice, which is exactly why the two belong in one effect.
/// </para>
/// </remarks>
public sealed record CreateTokenAndAttach(Domain.Models.CardDefinition Token) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // CR 701.3c: an Equipment can only be attached to a permanent that is still there, and
        // the source is the Equipment itself rather than whatever is resolving.
        var equipment = context.PhysicalSourceId;
        if (!context.State.TryGetObject(equipment, out var self) || self.Zone != Zone.Battlefield)
            return [];

        var germ = ObjectId.New();

        return
        [
            new ObjectCreated(germ, Token, context.ControllerId, context.ControllerId, Zone.Battlefield),
            new PermanentAttached(equipment, germ),
        ];
    }
}

/// <summary>
/// Runs effects against whatever the source is attached to (CR 701.3).
/// </summary>
/// <remarks>
/// "When this enters, tap enchanted creature." The enchanted creature is not a target — it was
/// chosen when the Aura was cast, and this is not choosing it again — so it cannot be an entry in
/// the ability's target list. But every verb that could apply to it already knows how to act on a
/// target, so rather than a tapping-the-attached effect and a destroying-the-attached effect and
/// four more, the inner effects are handed a context whose one target <em>is</em> the attached
/// permanent. Whatever the verb vocabulary learns, this learns with it.
/// <para>
/// Nothing attached means nothing happens, which is right: the Aura may have been moved, or the
/// creature may have died in response to the trigger.
/// </para>
/// </remarks>
public sealed record OnAttached(ImmutableList<IEffect> Effects) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var self)
            || self.Permanent?.AttachedTo is not { } attached
            || !context.State.TryGetObject(attached, out _))
        {
            return [];
        }

        var inner = context with { Targets = [Target.ToPermanent(attached)] };
        return [.. Effects.SelectMany(effect => effect.Resolve(inner))];
    }
}

/// <summary>Gives energy counters to the players a scope names (CR 107.4c).</summary>
public sealed record GainEnergy(Amount Count, PlayerScope Scope = PlayerScope.You) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var count = Count.In(context);
        return
        [
            .. PlayerScopes.Resolve(Scope, context).Select(who => new EnergyChanged(who, count)),
        ];
    }
}

/// <summary>
/// "You get an experience counter" (CR 122.1).
/// </summary>
/// <remarks>
/// Energy's twin, and separate from it because the two are different resources that different
/// cards read back: sixteen commanders hand out experience and eighteen cards multiply by "the
/// number of experience counters you have", none of which would be satisfied by a pile of energy.
/// <para>
/// The printed line is always exactly one counter, but the count is an <see cref="Amount"/> like
/// every other number in the engine, so a card that ever prints two - or two for each of
/// something - needs no new effect.
/// </para>
/// </remarks>
public sealed record GainExperience(Amount Count, PlayerScope Scope = PlayerScope.You) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var count = Count.In(context);
        return
        [
            .. PlayerScopes.Resolve(Scope, context)
                .Select(who => new ExperienceCountersChanged(who, count)),
        ];
    }
}

/// <summary>
/// Copies a spell on the stack some number of times (CR 707.10).
/// </summary>
/// <remarks>
/// One effect for both shapes the corpus prints. With a target index it copies what it is aimed
/// at — "copy target instant or sorcery spell". Without one it copies the spell that produced the
/// ability, which is what storm and replicate do, and there the source is a spell still on the
/// stack waiting below the trigger that is copying it.
/// <para>
/// The copies are created top-down so they resolve before the original, which is what makes storm
/// resolve as a stack of copies with the real spell underneath.
/// </para>
/// </remarks>
public sealed record CopySpell(Amount Count = default, int? TargetIndex = null) : IEffect
{
    /// <summary>
    /// How many copies, when the number has to be worked out from the game (CR 702.40a).
    /// </summary>
    /// <remarks>
    /// Storm's "for each spell cast before it this turn" is not a printed number and not X, which
    /// are the only two things an <see cref="Amount"/> can be. It lives here rather than widening
    /// Amount with a delegate, because one effect needing a computed count is not a reason to make
    /// every amount in the engine capable of holding code.
    /// </remarks>
    public Func<ResolutionContext, int>? CountFrom { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.SpellOnStack } aimed
                ? aimed.Subject
                : (ObjectId?)null
            : context.PhysicalSourceId;

        if (subject is not { } spellId
            || !context.State.TryGetObject(spellId, out var spell)
            || spell.Zone != Zone.Stack)
        {
            return [];
        }

        // "Copy it" with no number means once; a default Amount is zero. A computed count is
        // allowed to be zero, though — storm on the first spell of the turn copies nothing.
        var copies = CountFrom is { } counted ? Math.Max(0, counted(context)) : Math.Max(1, Count.In(context));
        if (copies == 0)
            return [];

        return
        [
            .. Enumerable.Range(0, copies).Select(_ => new SpellCopied(
                ObjectId.New(), spell.Card, context.ControllerId, spell.Targets)),
        ];
    }
}

/// <summary>
/// Creates a token that is a copy of a permanent (CR 707.2).
/// </summary>
/// <remarks>
/// What gets copied is the <em>copiable values</em> — the printed card, plus anything that copied
/// onto it already. Not its counters, not its damage, not what an Aura is doing to it: a copy of
/// a 2/2 bear wearing +3/+3 of Auras is a 2/2 bear. Taking the target's card is exactly that, and
/// it is right for the same reason a face-down permanent's card is left alone — the card is the
/// copiable part and the rest is effects on top of it.
/// <para>
/// The copy is marked a token so it stops existing when it leaves the battlefield (CR 111.7),
/// which is the one way it differs from the thing it copied.
/// </para>
/// </remarks>
/// <summary>
/// A bonus that grows with the number of blockers beyond the first (CR 702.23a) - rampage.
/// </summary>
/// <remarks>
/// The size is not known until it resolves, which is why it is not an ordinary pump: a pump is a
/// continuous effect whose name carries the numbers, so the name is generated here from a count
/// taken at resolution rather than baked in when the card was compiled.
/// <para>
/// The blockers are counted now rather than when the trigger fired. That is what the rule says -
/// the bonus is worked out on resolution - and it means a blocker removed in response makes the
/// bonus smaller, which is the interaction the card is played around.
/// </para>
/// </remarks>
public sealed record RampageBonus(int PerBlocker) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;
        var blockers = context.State.Combat.BlockersOf(sourceId);

        if (blockers.Count <= 1)
            return [];

        var bonus = PerBlocker * (blockers.Count - 1);

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.PumpId(bonus, bonus),
                [sourceId],
                context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Puts the card this ability is printed on onto the battlefield attacking (CR 702.49a).
/// </summary>
/// <remarks>
/// Ninjutsu, and the reason the ability carries an attack: the creature whose place the ninja
/// takes is back in its owner's hand by the time this resolves, so the defender it was attacking
/// has to have been written down when the cost was paid.
/// <para>
/// It arrives attacking without ever having been declared, so no "whenever this attacks" ability
/// of its own triggers - which is what <see cref="JoinedCombat"/> exists to express.
/// </para>
/// </remarks>
public sealed record PutSourceOntoBattlefieldAttacking : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var card)
            || card.Zone != Zone.Hand)
        {
            return [];
        }

        if (!context.State.TryGetObject(context.SourceId, out var onStack)
            || onStack.Ability?.JoiningAgainst is not { } joining)
        {
            return [];
        }

        var arriving = ObjectId.New();

        return
        [
            new ObjectMoved(
                context.PhysicalSourceId, arriving, Zone.Hand, Zone.Battlefield,
                card.OwnerId, MoveCause.Other),
            new PermanentTapped(arriving),
            new JoinedCombat(arriving, joining),
        ];
    }
}

/// <summary>
/// A token copy of the source for each other opponent, attacking them (CR 702.115a) - myriad.
/// </summary>
/// <remarks>
/// Only reaches anything in a game of three or more: with one opponent, that opponent is the
/// defending player and there is nobody else to attack. That is what the card says rather than a
/// shortcoming, and it is the clearest thing in the engine that the N-player priority model was
/// worth building.
/// <para>
/// The printed "you may" is read as "you do". Declining is a choice the engine cannot ask for
/// mid-resolution, and it is one almost nobody takes; the tokens are exiled at end of combat
/// either way, so nothing survives the turn that would not have.
/// </para>
/// </remarks>
public sealed record MyriadCopies : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;

        if (!context.State.TryGetObject(sourceId, out var original)
            || original.Zone != Zone.Battlefield
            || !context.State.Combat.Attackers.TryGetValue(sourceId, out var attacking))
        {
            return [];
        }

        // CR 707.3: the copiable values are what this permanent *is*, so a Clone that attacks
        // with myriad makes tokens of what it copied and not of Clone. Reading the printed card
        // made a myriad copy of a blank.
        var copied = TokenCards.AsToken(
            Characteristics.CardOf(context.State, context.Abilities, original));

        var events = new List<GameEvent>();

        foreach (var opponent in context.State.TurnOrder)
        {
            if (opponent == context.ControllerId
                || opponent == attacking.DefendingPlayer
                || context.State.GetPlayer(opponent).HasLost)
            {
                continue;
            }

            var token = ObjectId.New();

            events.Add(new ObjectCreated(
                token, copied, context.ControllerId, context.ControllerId, Zone.Battlefield));
            events.Add(new PermanentTapped(token));
            events.Add(new JoinedCombat(token, AttackTarget.Player(opponent)));
            events.Add(new DelayedTriggerCreated(
                Guid.NewGuid(),
                context.ControllerId,
                token,
                State.TurnStep.EndOfCombat,
                "exile",
                context.State.TurnNumber));
        }

        return events;
    }
}

/// <summary>
/// Tokens created already attacking whoever the source is attacking (CR 702.180a) - mobilize.
/// </summary>
/// <remarks>
/// Its own effect rather than <see cref="CreateToken"/> with flags, because "attacking" is not a
/// property a token can be created with: it is a place in the combat, and the token has to be put
/// there by joining the combat the source is already in. Which player that is cannot be known
/// until the trigger resolves, so it is read off the combat rather than chosen at compile time.
/// <para>
/// The tokens attack the same player the source does, not one each. That is the difference from
/// myriad, which spreads across the other opponents, and it is why this reads
/// <c>attacking.DefendingPlayer</c> instead of walking the turn order.
/// </para>
/// <para>
/// Sacrificed at the beginning of the next end step, which is a delayed trigger created here
/// rather than a rule the tokens carry - they are ordinary tokens, and nothing about them says
/// they are temporary except the ability that made them.
/// </para>
/// </remarks>
public sealed record MobilizeTokens(int Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Nothing at all if the source has left combat or the battlefield by the time this
        // resolves. A token created attacking nobody would sit on the board as a 1/1 that never
        // attacked and never got sacrificed, which is worse than no token.
        if (!context.State.Combat.Attackers.TryGetValue(context.PhysicalSourceId, out var attacking))
            return [];

        var warrior = new Domain.Models.CardDefinition
        {
            OracleId = "token-red-warrior",
            Name = "Warrior",
            CardTypes = Domain.Enums.CardType.Creature | Domain.Enums.CardType.Token,
            Subtypes = ["Warrior"],
            Power = 1,
            Toughness = 1,
            ColorIdentity = [Domain.Enums.ManaColor.Red],
            Colors = [Domain.Enums.ManaColor.Red],
        };

        var events = new List<GameEvent>();

        foreach (var _ in Enumerable.Range(0, Math.Max(0, Count)))
        {
            var token = ObjectId.New();

            events.Add(new ObjectCreated(
                token, warrior, context.ControllerId, context.ControllerId, Zone.Battlefield));
            events.Add(new PermanentTapped(token));
            events.Add(new JoinedCombat(token, attacking));
            events.Add(new DelayedTriggerCreated(
                Guid.NewGuid(),
                context.ControllerId,
                token,
                State.TurnStep.End,
                "sacrifice",
                context.State.TurnNumber));
        }

        return events;
    }
}

public sealed record CreateTokenCopy(
    Amount Count = default,
    int? TargetIndex = null,

    /// <summary>
    /// Whose card is copied, when it is neither a target nor this permanent.
    /// </summary>
    /// <remarks>
    /// "Whenever a creature dies, create a token that's a copy of that creature" names the
    /// creature the trigger was about. A null index used to mean this permanent, so there was no
    /// way to say that at all - and the pronoun branch beside it only worked when the sentence
    /// had already targeted something.
    /// </remarks>
    EffectSubject? Subject = null,

    /// <summary>Whether the token drops the legendary supertype (CR 707.2).</summary>
    bool ExceptNotLegendary = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = Subject is { } named
            ? Subjects.Resolve(context, named, TargetIndex ?? 0)
            : TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Permanent } aimed
                ? aimed.Subject
                : (ObjectId?)null
            : context.PhysicalSourceId;

        if (subject is not { } id)
            return [];

        // CR 608.2g: a copy of something that has already left uses last known information. That
        // is the ordinary case for "create a token that's a copy of that creature" on a death
        // trigger - by the time it resolves the creature is a card in a graveyard under a new id
        // (CR 400.7), so requiring it to still be on the battlefield made every such card do
        // nothing at all.
        var original = context.State.TryGetObject(id, out var present)
            ? present
            : context.ObjectBehind?.Invoke(id);

        if (original is null)
            return [];

        // A permanent that is still here has to be a permanent: "a copy of target creature" that
        // has since been exiled copies nothing, and only the trigger-subject reading is allowed
        // to reach back for something that has gone.
        if (Subject is not EffectSubject.TriggeringObject
            && original.Zone != Zone.Battlefield)
        {
            return [];
        }

        // CR 707.3: "a token that's a copy of target creature" copies what that permanent is
        // now, which is the copied card when something has already made it a copy of something
        // else. Only a permanent has copiable values worked out for it - a card in a graveyard
        // is read as itself, which is also the rule (CR 707.2).
        var copiable = original.Zone == Zone.Battlefield
            ? Characteristics.CardOf(context.State, context.Abilities, original)
            : original.Card;

        var copied = TokenCards.AsToken(copiable, dropLegendary: ExceptNotLegendary);

        var count = Math.Max(1, Count.In(context));

        return
        [
            .. Enumerable.Range(0, count).Select(_ => new ObjectCreated(
                ObjectId.New(), copied, context.ControllerId, context.ControllerId,
                Zone.Battlefield)),
        ];
    }
}

/// <summary>
/// The same card, marked as a token (CR 111.7).
/// </summary>
/// <remarks>
/// Written out field by field because <see cref="Domain.Models.CardDefinition"/> is a class and
/// not a record, so there is no <c>with</c>. Making it a record for this one call would change
/// equality across the whole application from reference to structural, on a type that carries
/// several collections — a much larger change than the one being asked for.
/// <para>
/// The images come along deliberately: a token copy is shown on the board as the thing it copied,
/// and a copy with no art is a copy the player cannot recognise.
/// </para>
/// </remarks>
internal static class TokenCards
{
    /// <param name="power">
    /// A size to print on the token instead of the card's own, for the few effects that make a
    /// copy of a different size - offspring's 1/1 is the reason this exists.
    /// </param>
    public static Domain.Models.CardDefinition AsToken(
        Domain.Models.CardDefinition card,
        int? power = null,
        int? toughness = null,
        string? oracleId = null,
        bool dropLegendary = false) => new()
        {
            OracleId = oracleId ?? card.OracleId,
            Name = card.Name,
            ManaCost = card.ManaCost,
            ManaCostRaw = card.ManaCostRaw,
            Cmc = card.Cmc,
            CardTypes = card.CardTypes | Domain.Enums.CardType.Token,
            Subtypes = card.Subtypes,
            // CR 707.2: "except it isn't legendary" is one of the exceptions a copy effect may
            // state. The supertype is dropped rather than the type line rewritten, because the
            // legend rule reads the computed supertypes.
            Supertypes = dropLegendary
                ? [.. card.Supertypes.Where(
                    s => !string.Equals(s, "Legendary", StringComparison.OrdinalIgnoreCase))]
                : card.Supertypes,
            OracleText = card.OracleText,
            Power = power ?? card.Power,
            Toughness = toughness ?? card.Toughness,
            StartingLoyalty = card.StartingLoyalty,
            Keywords = card.Keywords,
            ColorIdentity = card.ColorIdentity,
            Colors = card.Colors,

            // CR 707.8a: a token that is a copy of a double-faced permanent is itself
            // double-faced and can transform. Dropped, the token came back with one face and no
            // back, so nothing about it looked wrong and it could never turn over — the same
            // field, and the same silence, as the game log that was once caught losing it.
            Faces = card.Faces,
            ImageUriNormal = card.ImageUriNormal,
            ImageUriLarge = card.ImageUriLarge,
            ImageUriSmall = card.ImageUriSmall,
            ImageUriArtCrop = card.ImageUriArtCrop,
            ImageUriNormalBack = card.ImageUriNormalBack,
        };
}

/// <summary>
/// Exiles a target and remembers which permanent did it (CR 400.7).
/// </summary>
/// <remarks>
/// The first half of the "exile it until this leaves the battlefield" family. The link has to be
/// made here rather than by the returning half, because the card in exile is a new object with a
/// new id and only the effect that moved it knows both ends.
/// </remarks>
public sealed record ExileUntilSourceLeaves(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target
            || !context.State.TryGetObject(target.Subject, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        var exiled = ObjectId.New();

        return
        [
            new ObjectMoved(
                target.Subject, exiled, Zone.Battlefield, Zone.Exile,
                permanent.OwnerId, MoveCause.Exile),
            new ExiledUntilLeaves(exiled, context.PhysicalSourceId),
        ];
    }
}

/// <summary>
/// Returns everything this permanent exiled (CR 400.7).
/// </summary>
/// <remarks>
/// Cards come back to the battlefield under their owner's control, and they come back as new
/// objects with nothing they had before — no counters, no auras, no damage. Nothing here has to
/// arrange that: it is what a zone change means.
/// </remarks>
public sealed record ReturnExiledBySource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var source = context.PhysicalSourceId;

        return
        [
            .. context.State.Exile
                .Select(context.State.GetObject)
                .Where(card => card.ExiledBy == source)
                .Select(card => new ObjectMoved(
                    card.Id, ObjectId.New(), Zone.Exile, Zone.Battlefield,
                    card.OwnerId, MoveCause.Other)),
        ];
    }
}

/// <summary>
/// A player reveals their hand and somebody else takes a card from it (CR 701.16).
/// </summary>
/// <remarks>
/// Three printed sentences and one effect, because the middle one is the whole of it: revealing
/// is what makes the choice legal, and the discard is what the choice was for. Split into three
/// effects, the second would have nothing to choose from and the third nothing to discard.
/// </remarks>
public sealed record RevealAndTake(
    int TargetIndex,
    string FilterId,
    Zone Destination,
    int EffectIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Player } target
            || !context.State.Players.ContainsKey(target.Player))
        {
            return [];
        }

        var abilityId = context.AbilityId
            ?? (context.State.TryGetObject(context.SourceId, out var onStack)
                ? onStack.Ability?.AbilityId
                : null);

        return
        [
            new HandChoiceRequested(
                context.ControllerId,
                target.Player,
                context.PhysicalSourceId,
                abilityId,
                EffectIndex,
                string.Equals(FilterId, SearchFilters.AnyCard, StringComparison.Ordinal)
                    ? "a card"
                    : $"a {FilterId} card",
                FilterId),
        ];
    }
}

/// <summary>
/// "If that creature would die this turn, exile it instead" (CR 614.1c).
/// </summary>
/// <remarks>
/// A replacement effect the spell leaves behind rather than one printed on a permanent, and it
/// needs no new state at all: a floating effect is already "this thing applies to these objects
/// until the end of this turn", and the layer engine skips a definition id it does not know. The
/// replacement side recognises the name instead.
/// <para>
/// It is worth the trouble because it is not the same as destroying: a creature exiled this way
/// never reaches the graveyard, so nothing that watches for a death sees one and nothing can
/// bring it back.
/// </para>
/// </remarks>
public sealed record ExileInsteadOfDying(int TargetIndex = 0) : IEffect
{
    /// <summary>The name the replacement side looks for.</summary>
    public const string FloatingId = "dies-to-exile";

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target
            || !context.State.TryGetObject(target.Subject, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                FloatingId,
                [target.Subject],
                context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Gives the controller speed 1 if they have none at all (CR 702.179a).
/// </summary>
/// <remarks>
/// Does nothing to a player who is already moving, at any speed. That is the whole of "Start
/// your engines!" — everything after it is the automatic increase, which is a rule of the game
/// rather than anything printed on a card.
/// </remarks>
public sealed record StartYourEngines : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.State.GetPlayer(context.ControllerId).Speed == 0
            ? [new SpeedChanged(context.ControllerId, 1)]
            : [];
    }
}

/// <summary>
/// No combat damage is dealt for the rest of the turn (CR 615.1) — Fog.
/// </summary>
/// <remarks>
/// A shield over the whole combat rather than over one creature, so it affects nothing in
/// particular and carries an empty affected list. The combat damage step reads it and assigns
/// nothing at all, which is what prevention means: the damage is never dealt, so nothing that
/// watches for damage being dealt — lifelink, "whenever this deals damage" — sees anything.
/// <para>
/// Only combat damage. A card preventing <em>all</em> damage this turn would have to filter
/// every damage event in the game rather than one step, and is left unread.
/// </para>
/// </remarks>
public sealed record PreventAllCombatDamage : IEffect
{
    /// <summary>The name the combat damage step looks for.</summary>
    public const string FloatingId = "fog";

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(), FloatingId, [], context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Two creatures each deal damage equal to their power to the other (CR 701.12a).
/// </summary>
/// <remarks>
/// Both halves are worked out before either is dealt, because the damage is simultaneous: a
/// creature that dies to the fight still dealt its own. Reading the second power after applying
/// the first would make the loser hit for less, which is the classic way to get this wrong.
/// <para>
/// CR 701.12b: if either has left the battlefield by the time this resolves, neither deals or is
/// dealt damage — a fight needs two fighters.
/// </para>
/// </remarks>
public sealed record Fight(int TheirIndex, int? MyIndex = null, bool BothWays = true)
    : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var mine = MyIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Permanent } chosen
                ? chosen.Subject
                : (ObjectId?)null
            : context.PhysicalSourceId;

        if (mine is not { } myId
            || context.TargetAt(TheirIndex) is not { Kind: TargetKind.Permanent } theirs
            || !context.State.TryGetObject(myId, out var me)
            || !context.State.TryGetObject(theirs.Subject, out var them)
            || me.Zone != Zone.Battlefield
            || them.Zone != Zone.Battlefield)
        {
            return [];
        }

        var mineNow = Characteristics.Of(context.State, context.Abilities, me);
        var theirsNow = Characteristics.Of(context.State, context.Abilities, them);
        var myPower = mineNow.Power ?? 0;
        var theirPower = theirsNow.Power ?? 0;

        var events = new List<GameEvent>();

        // The source is named on each half so lifelink and wither can see who dealt it — a fight
        // is ordinary damage from one creature to another (CR 701.12a).
        //
        // Deathtouch is not carried by the source, though: it rides on the damage event, because
        // CR 704.5h remembers "was dealt damage by a deathtouch source" separately from how much.
        // Naming the source and leaving the flag off meant a fight with a deathtouch creature was
        // ordinary damage, so a 1/1 assassin fighting a 6/6 did one damage and nothing else.
        if (myPower > 0)
        {
            events.Add(new DamageMarked(
                theirs.Subject, myPower, mineNow.Has(KeywordAbility.Deathtouch), myId));
        }

        // "Deals damage equal to its power to target creature" is a fight with one half: the
        // damage goes one way and nothing comes back. Everything else about it is the same
        // question - whose power, computed when it resolves, from a source named so deathtouch
        // and lifelink still see who dealt it - which is why it is a flag here and not a second
        // effect that would have to get all of that right again.
        if (BothWays && theirPower > 0)
        {
            events.Add(new DamageMarked(
                myId, theirPower, theirsNow.Has(KeywordAbility.Deathtouch), theirs.Subject));
        }

        return events;
    }
}

/// <summary>
/// Exiles every card in a player's graveyard (CR 701.13a).
/// </summary>
/// <remarks>
/// One effect for the whole zone rather than a card at a time, because the cards are not chosen
/// and not targeted — the graveyard is, and everything in it goes. A player whose graveyard is
/// empty is not an error; there is simply nothing to move.
/// </remarks>
public sealed record ExileGraveyard(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Player } target
            || !context.State.Players.ContainsKey(target.Player))
        {
            return [];
        }

        return
        [
            .. context.State.GetPlayer(target.Player).Graveyard.Select(card => new ObjectMoved(
                card, ObjectId.New(), Zone.Graveyard, Zone.Exile, target.Player, MoveCause.Exile)),
        ];
    }
}

/// <summary>
/// Cascade: exile until something cheaper turns up, and offer it (CR 702.85a).
/// </summary>
/// <remarks>
/// Deferred like every other question, and for one more reason than usual: the cards that are not
/// taken go to the bottom of the library <em>in a random order</em>, and randomness lives on the
/// game so that every random outcome in a match comes from one seeded source and lands in the log.
/// </remarks>
public sealed record Cascade : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // CR 702.85a: "lesser mana value" means less than the spell that cascaded, which is the
        // object this ability belongs to — still on the stack, underneath this trigger.
        if (!context.State.TryGetObject(context.PhysicalSourceId, out var spell))
            return [];

        return
        [
            new CascadeRequested(context.ControllerId, context.PhysicalSourceId, spell.Card.Cmc),
        ];
    }
}

/// <summary>
/// Offers to show the top N cards and cast the ones sharing this spell's name (CR 702.60a).
/// </summary>
/// <remarks>
/// Cascade's shape with the search replaced by a name match, and like cascade it records that the
/// question is owed rather than asking it: an effect returns events, and a decision halts the
/// whole game. What name to match is read from the spell underneath rather than carried here, so
/// one definition serves every card that has the keyword.
/// </remarks>
public sealed record Ripple(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var many = Math.Max(0, Count.In(context));
        if (many == 0)
            return [];

        return [new RippleRequested(context.ControllerId, context.PhysicalSourceId, many)];
    }
}

/// <summary>Puts counters on a target permanent (CR 121.2).</summary>
public sealed record PutCounters(
    string Kind,
    Amount Count,
    int TargetIndex = 0,
    EffectSubject Subject = EffectSubject.Target) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } on)
            return [];

        // Counters live on permanents, and the engine says so: its reducer refuses a counter on
        // anything that is not on the battlefield. So an object that has left has to be checked
        // for here, not discovered there - "whenever a creature you control dies, put a +1/+1
        // counter on it" resolves with "it" meaning the card now in the graveyard (CR 400.7), and
        // that threw rather than doing nothing. Seven hundred and forty-six compiled effects were
        // one dies-trigger away from the same crash.
        if (!context.State.TryGetObject(on, out var subject) || subject.Zone != Zone.Battlefield)
            return [];

        return [new CountersChanged(on, Kind, Count.In(context))];
    }
}

/// <summary>
/// Which object a sentence is about, for the effects that can be about more than one thing.
/// </summary>
/// <remarks>
/// One reader rather than a copy inside each effect. The order of preference is the whole
/// content of the rule — a pronoun means the target if the sentence named one, otherwise
/// whatever the trigger was about, otherwise the permanent with the ability — and a second copy
/// of it is a second chance to get that order wrong.
/// </remarks>
internal static class Subjects
{
    public static ObjectId? Resolve(
        ResolutionContext context, EffectSubject subject, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(context);

        return subject switch
        {
            EffectSubject.Target =>
                context.TargetAt(targetIndex) is { Kind: TargetKind.Permanent } target
                    ? target.Subject
                    : null,

            EffectSubject.AttachedHost => Attachment.HostOf(context),

            // "It" with no target before it means whatever the trigger was about. When the
            // trigger was about no object at all, an attached permanent means its host before it
            // means itself: "at the beginning of the upkeep of enchanted creature's controller,
            // put a -1/-1 counter on it" is about the creature, and an Aura that read it as
            // itself put the counter on the Aura. Only then does it fall back to the permanent
            // with the ability, which is what a card that is on nothing must mean.
            EffectSubject.TriggerSubject =>
                context.SubjectObject
                ?? Attachment.HostOf(context)
                ?? context.PhysicalSourceId,

            // No fallback, deliberately. If the event was about no object then "that creature"
            // names nothing, and doing nothing is the honest answer where guessing is not.
            EffectSubject.TriggeringObject => context.SubjectObject,

            _ => context.PhysicalSourceId,
        };
    }
}

/// <summary>
/// Gives a target creature a bonus until end of turn — the pump effect (CR 611.2).
/// </summary>
/// <remarks>
/// Creates a continuous effect rather than editing the creature, so it applies in layer 7c and
/// ends during cleanup (CR 514.2) without anything having to remember to take it off.
/// </remarks>
public sealed record PumpUntilEndOfTurn(
    string DefinitionId,
    int TargetIndex = 0,
    EffectSubject Subject = EffectSubject.Target) : IEffect
{
    /// <summary>
    /// Whether the bonus lasts "until your next turn" rather than until end of turn (CR 611.2b).
    /// </summary>
    /// <remarks>
    /// The same effect with a longer duration, so it is a flag rather than a second record: what
    /// differs is one field on the event, and a parallel type would have been a second place for
    /// the layer lookup and the subject resolution to be got wrong.
    /// </remarks>
    public bool UntilYourNextTurn { get; init; }

    /// <summary>
    /// Whether the effect ends at cleanup, or lasts for as long as the game does (CR 611.2).
    /// </summary>
    /// <remarks>
    /// False is for the few effects that change a permanent and say nothing about when they
    /// stop: awaken stands a land up as a creature and it stays one. The duration lives on the
    /// effect rather than in the definition id because the id names <em>what</em> the change is,
    /// and the same change can be temporary on one card and permanent on another. It is
    /// exclusive with <see cref="UntilYourNextTurn"/> - a card prints one duration or none.
    /// </summary>
    public bool ForTheTurn { get; init; } = true;

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Through the shared subject resolver, like every other effect that can be aimed at
        // something other than a target. "That creature gets +1/+1" names the creature the
        // trigger was about, which is neither a target nor the permanent with the ability -
        // exalted is the card that shows the difference, because the creature attacking alone is
        // very often not the one with exalted on it.
        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } subject)
            return [];

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                DefinitionId,
                [subject],
                UntilYourNextTurn || !ForTheTurn ? null : context.State.TurnNumber)
            {
                UntilTurnOf = UntilYourNextTurn ? context.ControllerId : null,
            },
        ];
    }
}

/// <summary>
/// Makes a permanent become a copy of a target permanent (CR 613.2a, 707.2).
/// </summary>
/// <remarks>
/// The one continuous effect whose name cannot be worked out until it resolves. Every other
/// generated effect in this engine is a family with two numbers in it — <c>pump:+3/+3</c> — and
/// the compiler can write the name down while reading the card. A copy's name carries the whole
/// copied card, and which card that is depends on what is on the battlefield at the moment the
/// ability resolves, so the name is built here.
/// <para>
/// CR 707.3 is why it reads the copiable values rather than the printed card: a permanent that
/// has already become a copy of something else is copied as the thing it became.
/// </para>
/// <para>
/// CR 707.2b fixes those values now. The effect that lands in the log holds the card itself, so
/// the permanent that was copied may leave, die or change into something else without the copy
/// noticing — which is what the rule says and what an id pointing at an object could not do.
/// </para>
/// </remarks>
/// <param name="TargetIndex">Which target names the permanent whose values are copied.</param>
/// <param name="UntilEndOfTurn">
/// Whether the copy wears off (CR 514.2). False is a permanent change: "becomes a copy" with no
/// duration is what that permanent now is, and nothing has to take it back off again — the effect
/// is a layer rather than something written into the object.
/// </param>
/// <param name="Subject">
/// Which permanent becomes the copy. The source is the printed form on every card in the corpus
/// that says this — "{2}: This artifact becomes a copy of target artifact until end of turn" —
/// and the shared resolver is what lets a target or a trigger's subject be named instead without
/// this effect learning how.
/// </param>
public sealed record BecomeCopyOfTarget(
    int TargetIndex = 0,
    bool UntilEndOfTurn = true,
    EffectSubject Subject = EffectSubject.Source) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } me)
            return [];

        // CR 608.2b: a target that is no longer there fails to determine the information the
        // effect needs, and the effect does not happen. A copy of nothing would be a permanent
        // with no name at all.
        if (context.PeerAt(TargetIndex) is not { Zone: Zone.Battlefield } original)
            return [];

        var copied = Characteristics.CardOf(context.State, context.Abilities, original);

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.CopyId(copied),
                [me],
                UntilEndOfTurn ? context.State.TurnNumber : null),
        ];
    }
}

/// <summary>
/// Gives the source itself +N/+N until end of turn (CR 613.4).
/// </summary>
/// <remarks>
/// The same effect as <see cref="PumpUntilEndOfTurn"/> with nothing to aim it at: "~ gets +2/+2
/// until end of turn" is a firebreathing ability on the creature that has it, and a targeted
/// pump would ask the player to choose the only legal answer.
/// </remarks>
public sealed record PumpSourceUntilEndOfTurn(string DefinitionId) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The ability is on the stack; what it pumps is the permanent that produced it.
        var subject = context.PhysicalSourceId;

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(), DefinitionId, [subject], context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Pumps whatever the source is attached to, until end of turn (CR 701.3c).
/// </summary>
/// <remarks>
/// The activated twin of "enchanted creature gets +2/+2", which is a static. An Aura that can
/// pump on demand names no target - "enchanted creature" is whatever it is already on, and an
/// Aura attached to nothing is on its way to the graveyard anyway (CR 704.5m).
/// </remarks>
public sealed record PumpHostUntilEndOfTurn(string DefinitionId) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var aura)
            || aura.Permanent?.AttachedTo is not { } host)
        {
            return [];
        }

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(), DefinitionId, [host], context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Attaches the source permanent to a target permanent (CR 701.3).
/// </summary>
/// <remarks>
/// What an Equipment's equip ability does. The source is the permanent whose ability this is,
/// not the ability on the stack — the same distinction <see cref="PumpSourceUntilEndOfTurn"/>
/// makes, and for the same reason.
/// </remarks>
/// <summary>
/// Creates a token and attaches the source to it - job select (CR 702.182a).
/// </summary>
/// <remarks>
/// One effect rather than two, because the second half has to name the thing the first half
/// made and the subject vocabulary has no word for that: an effect returns events and the next
/// effect in the ability never sees them. Generating the id here is what lets the attachment be
/// written at all, and it is the same shape any "create a token, then attach this to it" line
/// needs.
/// </remarks>
public sealed record CreateTokenAndAttachSource(Domain.Models.CardDefinition Token) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The Equipment has to be on the battlefield to be attached to anything, and an enter
        // trigger whose source has already left attaches nothing rather than attaching a card
        // in a graveyard to a token.
        if (!context.State.TryGetObject(context.PhysicalSourceId, out var equipment)
            || equipment.Zone != Zone.Battlefield)
        {
            return [];
        }

        var token = ObjectId.New();

        return
        [
            new ObjectCreated(
                token, Token, context.ControllerId, context.ControllerId, Zone.Battlefield),
            new PermanentAttached(equipment.Id, token),
        ];
    }
}

/// <summary>Unattaches the source from whatever it is on - reconfigure (CR 702.151a).</summary>
/// <remarks>
/// The same event the attachment uses, with nothing to attach to. Held as its own effect rather
/// than an <see cref="AttachSourceTo"/> with no target, because "no target" already means "the
/// target was illegal and nothing happens" everywhere else.
/// </remarks>
public sealed record UnattachSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var equipment)
            || equipment.Permanent?.AttachedTo is null)
        {
            return [];
        }

        return [new PermanentAttached(equipment.Id, null)];
    }
}

public sealed record AttachSourceTo(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        var equipment = context.PhysicalSourceId;

        return [new PermanentAttached(equipment, target.Subject)];
    }
}

/// <summary>
/// Gives every creature its controller controls +N/+N until end of turn (CR 613.4).
/// </summary>
/// <remarks>
/// The set is fixed as the effect resolves, not re-evaluated afterwards: a creature that arrives
/// later does not get the bonus, which is what "creatures you control get" means as a one-shot
/// (CR 611.2c). A static anthem is a different thing and is a continuous effect on the source.
/// </remarks>
public sealed record PumpCreaturesYouControl(string DefinitionId) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var affected = context.State.Battlefield
            .Select(context.State.GetObject)
            .Where(o => o.ControllerId == context.ControllerId
                && Characteristics.IsCreature(context.State, context.Abilities, o))
            .Select(o => o.Id)
            .ToImmutableList();

        if (affected.IsEmpty)
            return [];

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(), DefinitionId, affected, context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Puts a regeneration shield on the source (CR 701.19).
/// </summary>
/// <remarks>
/// Nothing visible happens when this resolves. The shield waits, and is spent the next time the
/// permanent would be destroyed this turn — which is why "regenerate" reads as doing nothing
/// until something tries to kill it.
/// </remarks>
public sealed record Regenerate(EffectSubject Subject, int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ObjectId? shielded = null;

        if (Subject == EffectSubject.Target)
        {
            if (context.TargetAt(TargetIndex) is { Kind: TargetKind.Permanent } aimed)
                shielded = aimed.Subject;
        }
        else if (Subject == EffectSubject.AttachedHost)
        {
            shielded = Attachment.HostOf(context);
        }
        else
        {
            shielded = context.PhysicalSourceId;
        }

        return shielded is { } subject ? [new RegenerationShieldsChanged(subject, +1)] : [];
    }
}

/// <summary>What an effect is about, when the same effect can be about three things.</summary>
/// <remarks>
/// "Regenerate this creature", "regenerate target creature" and "regenerate enchanted creature"
/// are one action asked of three subjects. Naming the subject keeps them one effect: three
/// effects would be three places to get regeneration itself right.
/// </remarks>
public enum EffectSubject
{
    /// <summary>The permanent whose ability this is.</summary>
    Source,

    /// <summary>A target the ability chose.</summary>
    Target,

    /// <summary>What the source is attached to — an Aura's or Equipment's host (CR 701.3c).</summary>
    AttachedHost,

    /// <summary>
    /// The object the triggering event was about — what "that creature" refers to (CR 603.2).
    /// </summary>
    TriggerSubject,

    /// <summary>
    /// The object the triggering event was about, and nothing else (CR 603.2).
    /// </summary>
    /// <remarks>
    /// <see cref="TriggerSubject"/> with the fallbacks taken off, and the difference is the whole
    /// point. That one ends at the permanent with the ability when the event was about no object,
    /// which is right for "it" in a sentence that could only mean this card - and catastrophic for
    /// "that creature", which then means the wrong creature rather than none. An earlier attempt at
    /// reading "that creature" as the trigger's subject was reverted for exactly that: "whenever ~
    /// blocks a creature, destroy that creature" destroyed the blocker.
    /// <para>
    /// So this resolves to the subject or to nothing, and a sentence using it is only compiled
    /// when the trigger is known to supply one. Both halves are required; either alone is the bug.
    /// </para>
    /// </remarks>
    TriggeringObject,
}

/// <summary>What an Aura or Equipment is attached to (CR 701.3c).</summary>
/// <remarks>
/// Shared, because more than one effect needs it and every one of them needs the same two
/// answers: the permanent it is on, or nothing at all when it is on nothing — which is not an
/// error, it is an Aura on its way to the graveyard (CR 704.5m).
/// </remarks>
public static class Attachment
{
    public static ObjectId? HostOf(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.State.TryGetObject(context.PhysicalSourceId, out var attached)
            ? attached.Permanent?.AttachedTo
            : null;
    }
}

/// <summary>Scry N — look at the top N and put any of them on the bottom (CR 701.22).</summary>
public sealed record Scry(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [new LookAtTopRequested(context.ControllerId, Count.In(context), ToGraveyard: false)];
    }
}

/// <summary>Surveil N — the same, but the unwanted cards are milled (CR 701.25).</summary>
public sealed record Surveil(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [new LookAtTopRequested(context.ControllerId, Count.In(context), ToGraveyard: true)];
    }
}

/// <summary>Puts counters on the source itself (CR 122.1).</summary>
/// <remarks>
/// Renown, and every "put a +1/+1 counter on this creature" trigger. It aims at the permanent
/// that produced the ability rather than at a target, for the same reason the source pump does.
/// </remarks>
public sealed record PutCountersOnSource(string Kind, Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        if (!context.State.TryGetObject(subject, out var target) || target.Permanent is null)
            return [];

        return [new CountersChanged(subject, Kind, Count.In(context))];
    }
}

/// <summary>
/// Removes a time counter, and sacrifices the permanent when it was the last (CR 702.63a).
/// </summary>
/// <remarks>
/// Vanishing. The rule makes the sacrifice its own triggered ability, watching for the last
/// counter to leave; here the removal and the sacrifice happen in one resolution, so nobody gets
/// a window in between. The difference is visible only to a card that wants to respond to the
/// counter leaving, which is the same deviation evoke's sacrifice carries.
/// <para>
/// A permanent that has somehow lost all its counters already is left alone rather than
/// sacrificed: the ability removes a counter and only then asks whether that was the last, and
/// there was none to remove.
/// </para>
/// </remarks>
public sealed record TickVanishing : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        if (!context.State.TryGetObject(subject, out var permanent)
            || permanent.Permanent is not { } onBoard)
        {
            return [];
        }

        var left = onBoard.Counters.TryGetValue(CounterKinds.Time, out var many) ? many : 0;
        if (left <= 0)
            return [];

        var removed = new CountersChanged(subject, CounterKinds.Time, -1);

        return left > 1
            ? [removed]
            :
            [
                removed,
                new ObjectMoved(
                    subject, ObjectId.New(), Zone.Battlefield, Zone.Graveyard,
                    permanent.ControllerId, MoveCause.Sacrifice),
            ];
    }
}

/// <summary>
/// A +1/+1 counter, but only if the attack was on whoever is ahead (CR 702.105a) - dethrone.
/// </summary>
/// <remarks>
/// The condition is checked here rather than in the trigger because "the player with the most
/// life" is a fact about the board and the trigger grammar matches events. That makes it an
/// intervening-if in the wrong place: the real ability checks as it triggers and again as it
/// resolves (CR 603.4), and this checks only the second time, so a player who falls behind
/// between the declaration and the resolution stops the counter where the card would not.
/// <para>
/// Tied for most life counts, which is what the rule says and the opposite of what "the player
/// with the most life" reads like.
/// </para>
/// </remarks>
public sealed record DethroneCounter : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        if (!context.State.TryGetObject(subject, out var attacker) || attacker.Permanent is null)
            return [];

        if (!context.State.Combat.Attackers.TryGetValue(subject, out var attacking))
            return [];

        if (!context.State.Players.ContainsKey(attacking.DefendingPlayer))
            return [];

        var most = context.State.TurnOrder
            .Select(id => context.State.GetPlayer(id))
            .Where(player => !player.HasLost)
            .Max(player => player.Life);

        if (context.State.GetPlayer(attacking.DefendingPlayer).Life < most)
            return [];

        return [new CountersChanged(subject, CounterKinds.PlusOnePlusOne, 1)];
    }
}

/// <summary>
/// Returns the source from its graveyard to the battlefield, with counters (CR 702.92a).
/// </summary>
/// <remarks>
/// Undying and persist. The trigger fires as the creature dies, so by the time the ability
/// resolves the card is already in the graveyard and has a new identity there (CR 400.7) — which
/// is why this finds it by the id the move produced rather than by the id that died.
/// </remarks>
public sealed record ReturnSourceFromGraveyard(string CounterKind, Amount Counters) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The card is not where the ability says it is. A dies trigger fires from the
        // battlefield, and by the time it resolves the card has moved to the graveyard and taken
        // a new identity with it (CR 400.7) — so the id the ability remembers no longer names
        // anything. It is found again by what it is: the same card, in its owner's graveyard.
        if (!context.State.TryGetObject(context.SourceId, out var onStack))
            return [];

        var printed = onStack.Card;
        var owner = onStack.OwnerId;

        var found = context.State.GetPlayer(owner).Graveyard
            .Select(context.State.GetObject)
            .LastOrDefault(o => string.Equals(
                o.Card.OracleId, printed.OracleId, StringComparison.Ordinal));

        if (found is null)
            return [];

        var sourceId = found.Id;
        var arriving = ObjectId.New();

        return
        [
            new ObjectMoved(
                sourceId, arriving, Zone.Graveyard, Zone.Battlefield,
                owner, MoveCause.Return),
            new CountersChanged(arriving, CounterKind, Counters.In(context)),
        ];
    }
}

/// <summary>
/// "The next N damage that would be dealt to ~ this turn is dealt to target creature you control
/// instead" (CR 614.1b).
/// </summary>
/// <remarks>
/// Redirection, not prevention: the damage still happens and something else takes it. The shield
/// beside this one would have the wrong effect entirely - a creature that was meant to soak a
/// blow instead watches it vanish.
/// </remarks>
public sealed record RedirectDamage(Amount Amount, int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } destination
            || !context.State.TryGetObject(destination.Subject, out _)
            || !context.State.TryGetObject(context.PhysicalSourceId, out var from)
            || from.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new RedirectionChanged(
                from.Id, Math.Max(0, Amount.In(context)), destination.Subject),
        ];
    }
}

/// <summary>Prevents the next N damage to a target this turn (CR 615.1).</summary>
public sealed record PreventDamage(
    Amount Amount, int? TargetIndex = null, PlayerScope? Scope = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // "Dealt to you" names nobody to choose, so it is a scope rather than a target - the
        // same distinction every other effect here draws between the two.
        if (Scope is { } scope)
        {
            return
            [
                .. PlayerScopes.Resolve(scope, context)
                    .Select(id => new PlayerPreventionChanged(id, Amount.In(context))),
            ];
        }

        if (TargetIndex is not { } index || context.TargetAt(index) is not { } target)
            return [];

        // "Any target" is a permanent or a player, and the shield goes wherever it was aimed.
        // Only the permanent arm existed, so the spell resolved and left nothing behind whenever
        // a player was chosen.
        return target.Kind switch
        {
            TargetKind.Permanent => [new PreventionChanged(target.Subject, Amount.In(context))],
            TargetKind.Player => [new PlayerPreventionChanged(target.Player, Amount.In(context))],
            _ => [],
        };
    }
}

/// <summary>
/// Prevents damage to or from a described set of things (CR 615.1, 615.3).
/// </summary>
/// <remarks>
/// The other half of <see cref="PreventDamage"/>, and the half every prevention line that is not
/// "prevent the next N damage to target X" needs. Two things separate them:
/// <list type="bullet">
/// <item>
/// It shields by <em>description</em> rather than by target. "Prevent all damage that would be
/// dealt to creatures you control" names no target, so a countdown shield — which is created per
/// target — had nowhere at all to be put.
/// </item>
/// <item>
/// It can ask about the source. "Prevent all combat damage that would be dealt by creatures this
/// turn" is a question about who is dealing, and a shield sitting on the thing being hit cannot
/// answer it (CR 609.7).
/// </item>
/// </list>
/// <para>
/// <see cref="Amount"/> is CR 615.10's number, not CR 615.7's: it caps each damage event
/// separately and is never spent. Null means all of it, which is what the great majority print
/// and what the existing reader was faking with a shield of a million points.
/// </para>
/// </remarks>
public sealed record PreventDescribedDamage : IEffect
{
    /// <summary>The most prevented from any one damage event, or null for all of it.</summary>
    public int? Amount { get; init; }

    /// <summary>Whether it watches all damage, combat damage, or noncombat damage.</summary>
    public State.DamageKind Kind { get; init; }

    /// <summary>A chosen permanent or player it shields, for the targeted wordings.</summary>
    public int? TargetIndex { get; init; }

    /// <summary>Permanents answering this filter, in the shared vocabulary.</summary>
    public string? PermanentFilter { get; init; }

    /// <summary>Whose permanents those are, or null for anyone's.</summary>
    public PlayerScope? PermanentController { get; init; }

    /// <summary>A described set of players — "dealt to you", "dealt to players".</summary>
    public PlayerScope? Players { get; init; }

    /// <summary>A filter the damage's source has to answer — "dealt by creatures".</summary>
    public string? SourceFilter { get; init; }

    /// <summary>Whose sources those are, or null for anyone's.</summary>
    public PlayerScope? SourceController { get; init; }

    /// <summary>
    /// Whether it ends as the turn does (CR 514.2), which is what "this turn" means.
    /// </summary>
    /// <remarks>
    /// False is for a prevention a permanent's static ability generates, which lasts as long as
    /// the permanent does rather than as long as the turn.
    /// </remarks>
    public bool ForTheTurn { get; init; } = true;

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ObjectId? permanent = null;
        Guid? player = null;

        if (TargetIndex is { } index)
        {
            // A target that has gone leaves nothing to shield, and shielding everything instead
            // would be a strictly better card than the printed one.
            switch (context.TargetAt(index))
            {
                case { Kind: TargetKind.Permanent } aimedAtPermanent:
                    permanent = aimedAtPermanent.Subject;
                    break;
                case { Kind: TargetKind.Player } aimedAtPlayer:
                    player = aimedAtPlayer.Player;
                    break;
                default:
                    return [];
            }
        }

        return
        [
            new PreventionEffectCreated(new State.PreventionEffect
            {
                Id = Guid.NewGuid(),
                ControllerId = context.ControllerId,
                Amount = Amount,
                Kind = Kind,
                Permanent = permanent,
                PermanentFilter = PermanentFilter,
                PermanentController = PermanentController,
                Player = player,
                Players = Players,
                SourceFilter = SourceFilter,
                SourceController = SourceController,
                UntilEndOfTurn = ForTheTurn ? context.State.TurnNumber : null,
            }),
        ];
    }
}

/// <summary>Counters a target spell (CR 701.6).</summary>
/// <remarks>
/// A countered spell is put into its owner's graveyard from the stack; it does not resolve, so
/// none of its effects happen (CR 701.5a).
/// <para>
/// <paramref name="ToExile"/> is the rider a good many counterspells carry — "if that spell is
/// countered this way, exile it instead of putting it into its owner's graveyard". It changes
/// where the spell ends up and nothing else, which matters to every card that would otherwise
/// buy it back out of the graveyard.
/// </para>
/// </remarks>
public sealed record CounterTargetSpell(int TargetIndex = 0, bool ToExile = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.SpellOnStack } target)
            return [];

        if (!context.State.TryGetObject(target.Subject, out var spell))
            return [];

        // CR 701.5a: countering a spell moves it from the stack to its owner's graveyard, so
        // there has to be a spell on the stack to move. A target can stop being legal between
        // being chosen and this resolving (CR 608.2b) - something else countered it, or it
        // resolved - and a counterspell that then described a move out of a zone the object had
        // already left was refused by the reducer rather than doing nothing.
        if (spell.Zone != Zone.Stack)
            return [];

        // CR 701.6a: a spell that can't be countered simply isn't. Asked of the card rather than
        // of computed characteristics, because the layers describe permanents and this matters
        // while the thing is still a spell on the stack — there is no permanent to compute.
        if (context.Abilities.GrantedKeywords(spell.Card).HasFlag(KeywordAbility.CantBeCountered))
            return [];

        return
        [
            new ObjectMoved(
                target.Subject,
                ObjectId.New(),
                Zone.Stack,
                ToExile ? Zone.Exile : Zone.Graveyard,
                spell.ControllerId,
                ToExile ? MoveCause.Exile : MoveCause.Other),
        ];
    }
}

/// <summary>Who an effect is aimed at when it names a group rather than a target (CR 109.5).</summary>
public enum PlayerScope
{
    /// <summary>The controller of the spell or ability — "you".</summary>
    You,

    /// <summary>Every opponent of the controller.</summary>
    EachOpponent,

    /// <summary>Every player, the controller included.</summary>
    EachPlayer,

    /// <summary>
    /// Every player except the controller - "each other player".
    /// </summary>
    /// <remarks>
    /// The same set as <see cref="EachOpponent"/> at every table the engine plays today, and
    /// deliberately not folded into it: the two come apart the moment a card makes a player
    /// your teammate, and a card that said "other" would then be quietly wrong rather than
    /// unread.
    /// </remarks>
    EachOtherPlayer,

    /// <summary>
    /// Whoever controls the permanent the trigger was about - "that creature's controller".
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="TriggerSubject"/>, which is a player the event named directly.
    /// A land entering names no player at all; the one this asks for is read off the permanent.
    /// </remarks>
    SubjectController,

    /// <summary>
    /// The player the triggering event was about — "that player" (CR 603.2).
    /// </summary>
    /// <remarks>
    /// Only a triggered ability has one. Anywhere else it names nobody, and an effect scoped to
    /// it does nothing rather than falling back to the controller: "that player discards a card"
    /// made to mean "you discard a card" is a different and much worse card.
    /// </remarks>
    TriggerSubject,

    /// <summary>
    /// The player the source is attacking - "defending player" (CR 506.2).
    /// </summary>
    /// <remarks>
    /// Read off the combat rather than off the trigger, because an attack trigger names the
    /// attacker as its subject and not the player being attacked. Names nobody outside combat,
    /// and an effect scoped to it then does nothing rather than picking someone.
    /// </remarks>
    DefendingPlayer,

    /// <summary>
    /// The player the source is attached to — "enchanted player" (CR 303.4b).
    /// </summary>
    /// <remarks>
    /// A Curse is an Aura whose host is a player rather than a permanent, and every ability on
    /// one names that player: "enchanted player mills two cards", "~ deals damage to enchanted
    /// player". It is a scope rather than a target because nothing about the ability chooses it —
    /// it is decided once, when the Aura is attached, and CR 303.4m says the same word means the
    /// same player on any permanent attached to one, Aura or not.
    /// <para>
    /// Names nobody when the source is attached to nothing, which is the honest answer for an
    /// Aura that has come unattached rather than a reason to fall back to its controller. The
    /// state-based action puts it in the graveyard shortly afterwards anyway (CR 704.5n).
    /// </para>
    /// </remarks>
    EnchantedPlayer,
}

/// <summary>The players a scope names, in turn order (CR 101.4).</summary>
internal static class PlayerScopes
{
    public static IEnumerable<Guid> Resolve(PlayerScope scope, ResolutionContext context) =>
        scope switch
        {
            PlayerScope.You => [context.ControllerId],
            // Followed forward if the permanent has since moved on (CR 400.7): a creature that
            // died still names whoever controlled it.
            PlayerScope.SubjectController => context.SubjectObject is { } about
                && (context.State.TryGetObject(about, out var owner)
                    ? owner
                    : context.ObjectBehind?.Invoke(about)) is { } found
                ? [found.ControllerId]
                : [],

            PlayerScope.TriggerSubject => context.SubjectPlayer is { } subject
                && context.State.Players.ContainsKey(subject)
                ? [subject]
                : [],
            PlayerScope.DefendingPlayer =>
                context.State.Combat.Attackers.TryGetValue(
                    context.PhysicalSourceId, out var attacking)
                && context.State.Players.ContainsKey(attacking.DefendingPlayer)
                    ? [attacking.DefendingPlayer]
                    : [],

            // The permanent rather than the ability, because an ability on the stack is its own
            // object and is attached to nothing (CR 303.4b). A player who has left the game is
            // no longer one to be enchanted, so the scope names nobody rather than an empty seat.
            PlayerScope.EnchantedPlayer =>
                context.State.TryGetObject(context.PhysicalSourceId, out var attachedSource)
                && attachedSource.Permanent?.AttachedToPlayer is { } enchanted
                && context.State.Players.TryGetValue(enchanted, out var host)
                && !host.HasLost
                    ? [enchanted]
                    : [],
            PlayerScope.EachOpponent or PlayerScope.EachOtherPlayer => context.State.ApnapOrder()
                .Where(id => id != context.ControllerId && !context.State.GetPlayer(id).HasLost),
            _ => context.State.ApnapOrder().Where(id => !context.State.GetPlayer(id).HasLost),
        };

    /// <summary>
    /// The same question asked of a permanent rather than of a resolving spell or ability.
    /// </summary>
    /// <remarks>
    /// A static ability has no resolution to be the controller of, so "you" is whoever controls
    /// the permanent the ability is printed on. The three scopes a board question can answer are
    /// the only ones offered here: the rest read a trigger's subject or a combat, and neither
    /// exists at the moment a cost is being worked out (CR 601.2f).
    /// </remarks>
    public static IEnumerable<Guid> Around(
        PlayerScope scope, State.GameState state, Guid controllerId)
    {
        ArgumentNullException.ThrowIfNull(state);

        return scope switch
        {
            PlayerScope.You => [controllerId],
            PlayerScope.EachOpponent or PlayerScope.EachOtherPlayer => state.ApnapOrder()
                .Where(id => id != controllerId && !state.GetPlayer(id).HasLost),
            PlayerScope.EachPlayer => state.ApnapOrder().Where(id => !state.GetPlayer(id).HasLost),
            _ => [],
        };
    }
}

/// <summary>
/// Each named player discards cards (CR 701.9).
/// </summary>
/// <remarks>
/// Which card a player discards is theirs to choose (CR 701.9a), and the engine cannot make that
/// choice for them — so this discards from the front of the hand only when there is no choice to
/// make, and otherwise raises one. That is why it emits a <see cref="ChoiceRequested"/> rather
/// than picking: a discard the engine chose is a different game from the one the card describes.
/// </remarks>
public sealed record DiscardCards(
    Amount Count,
    PlayerScope Scope = PlayerScope.You,
    bool AtRandom = false,
    int? TargetIndex = null,
    bool WholeHand = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        // "Target player discards a card" names one player and the scope names none, which is
        // why the target wins outright rather than being added to what the scope found. A target
        // that has gone is not a scope to fall back on: the effect simply does nothing.
        var told = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Player } aimed
                ? (IEnumerable<Guid>)[aimed.Player]
                : []
            : PlayerScopes.Resolve(Scope, context);

        foreach (var who in told)
        {
            var hand = context.State.GetPlayer(who).Hand;
            if (hand.IsEmpty)
                continue;

            // "Discard your hand" is a count of however many they are holding, which is a
            // different question per player and so cannot be a number the compiler worked out.
            // Everything after it is the same, including the part that makes this easy: with no
            // choice left to make the engine may act.
            var count = WholeHand ? hand.Count : Count.In(context);
            if (!AtRandom && hand.Count <= count)
            {
                foreach (var card in hand)
                {
                    events.Add(new ObjectMoved(
                        card, ObjectId.New(), Zone.Hand, Zone.Graveyard, who, MoveCause.Discard));
                }

                continue;
            }

            events.Add(new DiscardRequested(who, Math.Min(count, hand.Count), AtRandom));
        }

        return events;
    }
}

/// <summary>
/// Puts the top cards of a library into its owner's graveyard (CR 701.17).
/// </summary>
/// <remarks>
/// Milling is not drawing: a player who cannot mill as many cards as the effect says mills as
/// many as they can and does not lose the game for it (CR 701.17b). That is why this stops at the
/// end of the library rather than emitting the empty-draw attempt that <see cref="DrawCards"/>
/// does.
/// </remarks>
/// <summary>A permanent connives - draw, then discard, then maybe grow (CR 701.50a).</summary>
/// <remarks>
/// The draw is done here and the discard is not, because which card goes is the player's to
/// choose and whether it was a land decides the counter. Both halves have to happen even when
/// the library is empty: drawing from nothing is a loss the player takes later (CR 704.5b), not
/// a reason for the rest of the ability to stop.
/// </remarks>
public sealed record Connive(Amount Count, int? TargetIndex = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var many = Math.Max(0, Count.In(context));
        if (many == 0)
            return [];

        // "Target creature you control connives" - the permanent that connives is the one the
        // sentence names, and it is *its* controller who draws and discards and *it* that grows.
        // Defaulting both to the source would have drawn the right cards onto the wrong player
        // at any table where the target was not the caster's own.
        var conniver = context.PhysicalSourceId;

        if (TargetIndex is { } index)
        {
            if (context.TargetAt(index) is not { Kind: TargetKind.Permanent } aimed)
                return [];

            conniver = aimed.Subject;
        }

        if (!context.State.TryGetObject(conniver, out var permanent))
            return [];

        var who = permanent.Zone == Zone.Battlefield
            ? Characteristics.Of(context.State, context.Abilities, permanent).ControllerId
            : context.ControllerId;

        var events = new List<GameEvent>(Drawing.From(context, who, many));

        // One request per card: CR 701.50a is written for one, and a permanent that connives
        // twice asks twice - each discard is judged on its own for the counter.
        for (var i = 0; i < many; i++)
            events.Add(new ConniveRequested(who, conniver));

        return events;
    }
}

/// <summary>"Becomes the creature type of your choice" (CR 205.1b).</summary>
/// <remarks>
/// The colour choice's twin. A null index means the source is choosing for itself, which is what
/// every card with this line says - "{1}: this creature becomes the creature type of your
/// choice until end of turn".
/// </remarks>
public sealed record ChooseCreatureTypeForTarget(int? TargetIndex = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var affected = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Permanent } aimed
                ? ImmutableList.Create(aimed.Subject)
                : ImmutableList<ObjectId>.Empty
            : ImmutableList.Create(context.PhysicalSourceId);

        return affected.IsEmpty
            ? []
            : [new CreatureTypeChoiceRequested(
                context.ControllerId, context.PhysicalSourceId, affected)];
    }
}

/// <summary>"Discover N" - cascade with a hand fallback (CR 701.57a).</summary>
/// <remarks>
/// The exiling is not a decision, so nothing is asked here: what the player gets is the offer at
/// the end, taken by casting the card like any other. The only two things that separate this
/// from cascade are the comparison - N or less, against cascade's strictly less - and where a
/// declined card ends up.
/// </remarks>
public sealed record Discover(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            new DiscoverRequested(
                context.ControllerId, context.PhysicalSourceId, Count.In(context)),
        ];
    }
}

/// <summary>Creates an Incubator token with counters on it (CR 701.53a, 111.10i).</summary>
/// <remarks>
/// The token is double-faced, which the engine already models: a card with two faces transforms
/// through <c>PermanentTransformed</c>, and nothing about that cares whether the card came from
/// a printing or from here. The counters go on in the same breath as the token is made, because
/// CR 701.53a says it enters with them - a creature that arrived at 0/0 and grew a moment later
/// would already have died to state-based actions.
/// </remarks>
public sealed record Incubate(Amount Count) : IEffect
{
    /// <summary>The token CR 111.10i describes, front face first.</summary>
    internal static Domain.Models.CardDefinition Token { get; } = new()
    {
        OracleId = "token-incubator",
        Name = "Incubator",
        CardTypes = Domain.Enums.CardType.Artifact | Domain.Enums.CardType.Token,
        Subtypes = ["Incubator"],
        OracleText = "{2}: Transform this artifact.",
        Faces =
        [
            new Domain.Models.CardFace
            {
                Name = "Incubator",
                TypeLine = "Artifact Token — Incubator",
                CardTypes = Domain.Enums.CardType.Artifact | Domain.Enums.CardType.Token,
                Subtypes = ["Incubator"],
                OracleText = "{2}: Transform this artifact.",
            },
            new Domain.Models.CardFace
            {
                Name = "Phyrexian Token",
                TypeLine = "Artifact Creature Token — Phyrexian",
                CardTypes = Domain.Enums.CardType.Artifact
                    | Domain.Enums.CardType.Creature
                    | Domain.Enums.CardType.Token,
                Subtypes = ["Phyrexian"],
                Power = 0,
                Toughness = 0,
            },
        ],
    };

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var counters = Math.Max(0, Count.In(context));
        var id = ObjectId.New();

        return
        [
            new ObjectCreated(
                id, Token, context.ControllerId, context.ControllerId, Zone.Battlefield),
            new CountersChanged(id, State.CounterKinds.PlusOnePlusOne, counters),
        ];
    }
}

/// <summary>"Venture into the dungeon" (CR 701.49).</summary>
/// <remarks>
/// Three cases in the rule and all three are here, because they are one instruction and a card
/// that could only do the second would stall the first time anybody played it:
/// <list type="bullet">
/// <item>No dungeon in the command zone (CR 701.49a): one is put there and the marker goes on its
/// topmost room. The dungeon is a real object in the command zone, so its room abilities are
/// found by the ordinary trigger scan rather than being run from inside this resolution - which
/// is what lets a room target something.</item>
/// <item>One arrow out of the current room (CR 701.49b): the marker moves.</item>
/// <item>Several arrows: the player chooses, and the venture finishes when they answer. The
/// question is emitted rather than resolved here for the reason every deferred question is - an
/// effect cannot stop half way through and wait.</item>
/// </list>
/// The fourth case in the rule, venturing while already on the bottommost room (CR 701.49c),
/// cannot be reached in a settled game: CR 309.6 removes that dungeon from the game as a
/// state-based action before anybody has priority again, so the player owns none by the time the
/// next venture happens and the first case applies. It returns nothing rather than guessing.
/// </remarks>
public sealed record VentureIntoTheDungeon : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var who = context.ControllerId;
        var state = context.State;

        if (Dungeons.OwnedBy(state, who) is not { } owned)
        {
            var card = Dungeons.CardFor(Dungeons.Default);

            return
            [
                new ObjectCreated(ObjectId.New(), card, who, who, Zone.Command),
                new VentureMarkerMoved(
                    who, card.Name, Dungeons.Definition(card.Name)!.Top.Name),
            ];
        }

        var dungeon = owned.Card.Name;
        var next = Dungeons.RoomsAfter(dungeon, state.GetPlayer(who).DungeonRoom);

        return next.Count switch
        {
            0 => [],
            1 => [new VentureMarkerMoved(who, dungeon, next[0])],
            _ => [new VentureRoomRequested(who, dungeon, next)],
        };
    }
}

/// <summary>"The Ring tempts you" (CR 701.54a).</summary>
/// <remarks>
/// Two things in order: the temptation is counted, then a Ring-bearer is chosen. The count goes
/// up first because the emblem's abilities are "as long as the Ring has tempted you N or more
/// times", and the creature about to be chosen should already be under the ability it earns.
/// </remarks>
public sealed record TheRingTemptsYou : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            new RingTempted(context.ControllerId),
            new RingBearerRequested(context.ControllerId),
        ];
    }
}

/// <summary>Encore: a token copy per opponent, each aimed at one of them (CR 702.141a).</summary>
/// <remarks>
/// The tokens are not created attacking - encore is sorcery-speed, so there is no combat yet.
/// They are created with a requirement instead: attack, and attack that opponent. Both halves
/// travel in the continuous effect's id, because a token has nowhere else to keep them.
/// </remarks>
public sealed record EncoreCopies : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The cost exiled this card before the effect resolved, so the source id names nothing
        // any more - a card that changes zones is a new object (CR 400.7). The object it became
        // is found by the link the move left behind, which is the same recovery a dies-trigger
        // needs to know what "another" excludes.
        var card = context.State.TryGetObject(context.PhysicalSourceId, out var original)
            ? original.Card
            : context.State.Objects.Values
                .FirstOrDefault(o => o.PreviousId == context.PhysicalSourceId)?.Card;

        if (card is null)
            return [];

        var copied = TokenCards.AsToken(card, oracleId: "token-encore-" + card.OracleId);
        var events = new List<GameEvent>();

        foreach (var opponent in context.State.ApnapOrder())
        {
            if (opponent == context.ControllerId || context.State.GetPlayer(opponent).HasLost)
                continue;

            var token = ObjectId.New();

            events.Add(new ObjectCreated(
                token, copied, context.ControllerId, context.ControllerId, Zone.Battlefield));

            events.Add(new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.MustAttackPlayerId(opponent),
                [token],
                context.State.TurnNumber));

            // CR 702.141a: sacrificed at the beginning of the next end step, whatever happened
            // to them in between - which is what keeps encore from being a permanent army.
            events.Add(new DelayedTriggerCreated(
                Guid.NewGuid(),
                context.ControllerId,
                token,
                State.TurnStep.End,
                "sacrifice",
                context.State.TurnNumber));
        }

        return events;
    }
}

/// <summary>Offers the exploit sacrifice (CR 702.110a).</summary>
/// <remarks>
/// The offer and the choice are one question rather than a yes-or-no followed by a which-one:
/// "you may sacrifice a creature" is answered by naming one or by declining, and splitting it
/// would ask the player twice for one decision.
/// </remarks>
public sealed record OfferExploit : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return [new ExploitRequested(context.ControllerId, context.PhysicalSourceId)];
    }
}

/// <summary>Copies a creature token its controller chooses (CR 701.36a).</summary>
/// <remarks>
/// Not a target: populate chooses on resolution, so nothing is picked when the spell is cast and
/// nothing about it can be made illegal in between. That is the difference between this and the
/// ordinary "create a token that's a copy of target creature".
/// </remarks>
public sealed record Populate(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var many = Math.Max(0, Count.In(context));

        // "Populate twice" is two separate choices, not one choice copied twice - the second may
        // well pick the token the first just made.
        return [.. Enumerable.Range(0, many).Select(_ => new PopulateRequested(context.ControllerId))];
    }
}

/// <summary>Looks at the top two and manifests one of them (CR 701.62a).</summary>
/// <remarks>
/// Nothing moves here: which card is manifested is the player's to choose, and the other goes
/// to the graveyard only once that is settled. Fewer than two cards is not a failure - the
/// player looks at what there is and manifests one of those.
/// </remarks>
public sealed record ManifestDread : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var looked = context.State.GetPlayer(context.ControllerId).Library.Take(2).ToImmutableArray();
        if (looked.IsEmpty)
            return [];

        return [new ManifestDreadRequested(context.ControllerId, looked)];
    }
}

/// <summary>
/// Exiles the top cards of your library and lets you play them this turn (CR 601.3e).
/// </summary>
/// <remarks>
/// One effect for two printed sentences, because the second names what the first produced and an
/// effect never sees the events of the one before it. The same reason job select creates its
/// token and attaches to it in one place.
/// </remarks>
public sealed record ExileTopAndMayPlay(
    Amount Count, bool ThroughOwnersNextTurn = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var many = Math.Max(0, Count.In(context));
        if (many == 0)
            return [];

        var events = new List<GameEvent>();
        var library = context.State.GetPlayer(context.ControllerId).Library;

        foreach (var card in library.Take(many))
        {
            var exiled = ObjectId.New();

            events.Add(new ObjectMoved(
                card, exiled, Zone.Library, Zone.Exile, context.ControllerId, MoveCause.Exile));

            events.Add(new CardMayBePlayed(
                exiled, context.State.TurnNumber, ThroughOwnersNextTurn));
        }

        return events;
    }
}

/// <summary>Manifests the top card of a library (CR 701.40a).</summary>
/// <remarks>
/// The card arrives face down and becomes a 2/2 with no text, which the characteristics already
/// know how to compute for a face-down permanent - so nothing here describes a 2/2, and turning
/// it face up needs no remembered "real" card because the card was never changed.
/// <para>
/// CR 701.40e: several are manifested one at a time, which is what the loop is - each takes the
/// card that is on top once the one before it has gone.
/// </para>
/// </remarks>
public sealed record Manifest(Amount Count, PlayerScope Scope = PlayerScope.You) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        foreach (var who in PlayerScopes.Resolve(Scope, context))
        {
            var library = context.State.GetPlayer(who).Library;

            foreach (var card in library.Take(Math.Max(0, Count.In(context))))
            {
                var arrived = ObjectId.New();

                events.Add(new ObjectMoved(
                    card, arrived, Zone.Library, Zone.Battlefield, who, MoveCause.Other));
                events.Add(new CardManifested(arrived));
            }
        }

        return events;
    }
}

public sealed record MillCards(
    Amount Count, PlayerScope Scope = PlayerScope.You, int? TargetIndex = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        // A named target wins outright over the scope, the same way a targeted discard does: the
        // card says who, and a target that has gone is not a scope to fall back on.
        var told = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Player } aimed
                ? (IEnumerable<Guid>)[aimed.Player]
                : []
            : PlayerScopes.Resolve(Scope, context);

        foreach (var who in told)
        {
            var library = context.State.GetPlayer(who).Library;
            foreach (var card in library.Take(Count.In(context)))
            {
                events.Add(new ObjectMoved(
                    card, ObjectId.New(), Zone.Library, Zone.Graveyard, who, MoveCause.Mill));
            }
        }

        return events;
    }
}

/// <summary>Exiles the top cards of a library (CR 701.19).</summary>
public sealed record ExileFromTopOfLibrary(Amount Count, PlayerScope Scope = PlayerScope.You) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        foreach (var who in PlayerScopes.Resolve(Scope, context))
        {
            var library = context.State.GetPlayer(who).Library;
            foreach (var card in library.Take(Count.In(context)))
            {
                events.Add(new ObjectMoved(
                    card, ObjectId.New(), Zone.Library, Zone.Exile, who, MoveCause.Exile));
            }
        }

        return events;
    }
}

/// <summary>
/// Every player in a group gains or loses life (CR 119.3).
/// </summary>
/// <remarks>
/// Separate from <see cref="ChangeLife"/>, which is about one player named by a target. "Each
/// opponent loses 2 life" targets nobody at all, so it cannot be expressed as a target index —
/// and a card that targeted every opponent would be a different card, stopped by hexproof.
/// </remarks>
public sealed record ChangeLifeOfEach(Amount Amount, PlayerScope Scope) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            .. PlayerScopes.Resolve(Scope, context).Select(who =>
                new LifeChanged(
                    who, Amount.In(context), context.State.GetPlayer(who).Life + Amount.In(context))),
        ];
    }
}

/// <summary>
/// Shuffles a library, optionally taking a graveyard with it (CR 701.24a).
/// </summary>
/// <remarks>
/// Asks rather than acts. The resulting order is the game's own decision and must come from the
/// seeded source so a replay reproduces it, and an effect has no access to that - so this emits
/// a request the engine answers at the next settle, the way cascade and search already do.
/// </remarks>
/// <summary>Shuffles the card this spell is into its owner's library (CR 701.24a).</summary>
/// <remarks>
/// The beacons: a sorcery that goes back into the deck instead of to the graveyard. The card is
/// still on the stack as this resolves and reaches the graveyard a moment later under a new id
/// (CR 400.7), so the shuffle names the id it has now and the settle follows it forward - the
/// same way a deferred branch finds the permanent its spell became.
/// </remarks>
public sealed record ShuffleSourceIntoLibrary : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var card))
            return [];

        return [new ShuffleRequested(card.OwnerId, [context.PhysicalSourceId])];
    }
}

public sealed record ShuffleLibrary(
    PlayerScope Whose = PlayerScope.You, bool GraveyardFirst = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            .. PlayerScopes.Resolve(Whose, context)
                .Select(who => new ShuffleRequested(
                    who,
                    GraveyardFirst
                        ? context.State.GetPlayer(who).Graveyard
                        : [])),
        ];
    }
}

/// <summary>
/// Every player in a group draws (CR 121.3).
/// </summary>
/// <remarks>
/// Not the same as several <see cref="DrawCards"/> in a row: the draws happen in turn order
/// starting with the active player (CR 101.4), and a player who runs out of library still
/// records the attempt so the rest of the effect resolves and the loss is a state-based action
/// afterwards.
/// </remarks>
public sealed record DrawEach(Amount Count, PlayerScope Scope) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();
        var count = Count.In(context);

        foreach (var who in PlayerScopes.Resolve(Scope, context))
        {
            // Each player's own library, and each player's own run of draws: the drawn cards
            // leave the library as the events are folded, so the indices below are read against
            // a library that has not moved yet and every draw names a distinct card.
            var library = context.State.GetPlayer(who).Library;

            for (var i = 0; i < count; i++)
            {
                if (i >= library.Count)
                {
                    events.Add(new DrawFromEmptyLibraryAttempted(who));
                    break;
                }

                events.Add(new ObjectMoved(
                    library[i], ObjectId.New(), Zone.Library, Zone.Hand, who, MoveCause.Draw));
            }
        }

        return events;
    }
}

/// <summary>
/// The source deals damage to each player in a group (CR 119.3).
/// </summary>
/// <remarks>
/// Damage rather than life loss, which is a real distinction: damage can be prevented and it
/// triggers lifelink on the source (CR 702.15b), while life loss does neither.
/// </remarks>
public sealed record DamageEach(Amount Amount, PlayerScope Scope) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            .. PlayerScopes.Resolve(Scope, context).Select(who =>
                new PlayerDamaged(
                    who, context.PhysicalSourceId, Amount.In(context), IsCombat: false)),
        ];
    }
}

/// <summary>Returns the source card from its owner's graveyard to their hand (CR 701.19).</summary>
public sealed record ReturnSourceToHand : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The source may be the card in the graveyard already, or the permanent that died and
        // became it a moment ago - a death trigger sees the second (CR 400.7). Both have to
        // reach the same card, so the id is followed forward when it no longer names anything.
        var card = context.State.TryGetObject(context.PhysicalSourceId, out var live)
            ? live
            : context.ObjectBehind?.Invoke(context.PhysicalSourceId);

        if (card is not { Zone: Zone.Graveyard })
            return [];

        return
        [
            new ObjectMoved(
                card.Id, ObjectId.New(), Zone.Graveyard, Zone.Hand, card.OwnerId,
                MoveCause.Return),
        ];
    }
}

/// <summary>Returns the source itself from the battlefield to its owner's hand (CR 400.7).</summary>
/// <remarks>
/// The tail of a great many enters triggers: the permanent arrives, does something, and bounces
/// itself. It finds the source through the ability on the stack, because by the time an ability
/// resolves the object resolving is the ability and not the permanent that made it.
/// </remarks>
public sealed record ReturnSourceFromBattlefield : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;

        if (!context.State.TryGetObject(sourceId, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new ObjectMoved(
                sourceId, ObjectId.New(), Zone.Battlefield, Zone.Hand, permanent.OwnerId,
                MoveCause.Return),
        ];
    }
}

/// <summary>
/// Weakens every creature blocking the source, except those with a given keyword (CR 702.25a).
/// </summary>
/// <remarks>
/// Flanking. The creatures it affects are neither targets nor the source: they are whatever
/// happened to block, which is a question only the combat state can answer. That is why this is
/// its own effect rather than a pump with a target index — there is no target to index, and the
/// set is not known until blockers are declared.
/// </remarks>
/// <summary>Every creature blocking the source is sacrificed at end of combat (CR 701.54c).</summary>
/// <remarks>
/// The Ring's third ability. Delayed rather than immediate, and that is the whole of it: the
/// blocker deals and takes its combat damage first, and only then goes. Sacrificed by its own
/// controller, which the delayed trigger records so it is their permanent that leaves.
/// </remarks>
public sealed record SacrificeBlockersAtEndOfCombat : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;
        if (!context.State.Combat.Blockers.TryGetValue(sourceId, out var blockers))
            return [];

        var events = new List<GameEvent>();

        foreach (var id in blockers)
        {
            if (!context.State.TryGetObject(id, out var blocker))
                continue;

            events.Add(new DelayedTriggerCreated(
                Guid.NewGuid(),
                Characteristics.Of(context.State, context.Abilities, blocker).ControllerId,
                id,
                State.TurnStep.EndOfCombat,
                "sacrifice",
                context.State.TurnNumber));
        }

        return events;
    }
}

public sealed record PumpBlockersOfSource(string DefinitionId, KeywordAbility Except) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;
        if (!context.State.Combat.Blockers.TryGetValue(sourceId, out var blockers))
            return [];

        var affected = blockers
            .Where(id => context.State.TryGetObject(id, out var blocker)
                // CR 702.25a: a blocker that has the keyword itself is spared, which is what
                // makes flanking creatures able to block each other.
                && !Characteristics.Of(context.State, context.Abilities, blocker).Has(Except))
            .ToImmutableList();

        return affected.IsEmpty
            ? []
            :
            [
                new ContinuousEffectCreated(
                    Guid.NewGuid(), DefinitionId, affected, context.State.TurnNumber),
            ];
    }
}

/// <summary>
/// "You may pay [cost]. If you do, ... If you don't, ..." (CR 601.2b).
/// </summary>
/// <remarks>
/// The one effect whose <em>rest</em> depends on an answer, which is why it is built the way it
/// is. It does not ask; it records that the question is owed, and the engine asks at the next
/// settle and then runs whichever branch the answer names.
/// <para>
/// <see cref="EffectIndex"/> is how the branch is found again. The engine cannot hold a
/// continuation — a game has to replay from its log, and a closure is not in a log — so the
/// answer carries a locator back to this effect inside the card's own definition, and the branch
/// is read from the card a second time. That is also why the branches are data on the record
/// rather than something captured: they have to survive being looked up rather than remembered.
/// </para>
/// <para>
/// The cost is mana, life, energy — or a selection, when <see cref="ChosenKind"/> is set. That
/// last one turns the question from a yes/no into a pick, because "unless that player discards a
/// card" cannot be answered without naming the card. It stays <em>one</em> question: asking
/// "will you pay?" and then "with what?" lets a player answer yes and then have nothing legal to
/// name, and gives the log two answers to keep in step where the rules have one decision. A
/// chosen cost does <em>not</em> scale with <see cref="TimesCounter"/> — that repeats the printed
/// mana text and nothing repeats the count — so cumulative upkeep's non-mana form would ask for
/// one of something on its fourth turn as readily as on its first, and is not built on this yet.
/// </para>
/// </remarks>
/// <param name="AskTargetController">
/// When set, the offer goes to the controller of that target rather than to this spell's
/// controller — "counter target spell unless its controller pays {2}". The branches still belong
/// to this spell, so "if you don't" is what happens when <em>they</em> decline.
/// </param>
/// <param name="ChosenKind">
/// When set, the cost is a selection rather than a price: sacrifice a permanent, discard a card,
/// return a permanent to its owner's hand (CR 118.12a). The kinds that may be asked for are the
/// ones <c>Game.PayableFor</c> can offer and <c>Game.TakeChosenPayment</c> can move; a kind
/// neither knows is declined rather than charged, which is the safe direction — a cost that
/// cannot be paid is not paid (CR 118.3).
/// </param>
/// <param name="ChosenCount">
/// How many objects the chosen cost takes. Partial payments are not payments (CR 601.2h), so
/// fewer picks than this is a decline and not a discount.
/// </param>
/// <param name="ChosenFilterId">
/// Which objects qualify, as a <see cref="SearchFilters"/> id — "creature" for "sacrifice a
/// creature". The default accepts anything, which is what "discard a card" means.
/// </param>
public sealed record MayPay(
    Mana.ManaCostSpec Cost,
    ImmutableList<IEffect> IfYouDo,
    ImmutableList<IEffect> IfYouDont,
    int EffectIndex = 0,
    int? AskTargetController = null,
    bool AskSubjectPlayer = false,
    string? YesLabel = null,
    string? NoLabel = null,
    string? TimesCounter = null,
    int EnergyCost = 0,
    int LifeCost = 0,
    ChosenCostKind? ChosenKind = null,
    int ChosenCount = 1,
    string ChosenFilterId = SearchFilters.AnyCard) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var abilityId = context.AbilityId
            ?? (context.State.TryGetObject(context.SourceId, out var onStack)
                ? onStack.Ability?.AbilityId
                : null);

        var asked = context.ControllerId;

        // Ward: the offer goes to whoever cast the spell that targeted this, which the trigger
        // recorded as its subject because the event naming them is long past by now.
        if (AskSubjectPlayer)
        {
            if (context.SubjectPlayer is not { } subject
                || !context.State.Players.ContainsKey(subject))
            {
                return [];
            }

            asked = subject;
        }

        if (AskTargetController is { } index)
        {
            // The offer belongs to whoever controls the thing this is aimed at. If that has gone
            // — the spell was countered by something else first — there is nobody to ask and the
            // effect does nothing rather than asking the wrong player.
            if (context.TargetAt(index) is not { } target
                || !context.State.TryGetObject(target.Subject, out var aimedAt))
            {
                return [];
            }

            asked = aimedAt.ControllerId;
        }

        // Cumulative upkeep asks for its cost once per counter (CR 702.24a). The cost travels
        // to the player as printed text, so charging it several times is the printed cost written
        // out several times - which is also how the card reads it aloud.
        var asking = Cost.ToString();

        if (TimesCounter is { } kind)
        {
            var counters = context.State.TryGetObject(context.PhysicalSourceId, out var counted)
                ? counted.Permanent?.Counters.GetValueOrDefault(kind, 0) ?? 0
                : 0;

            asking = string.Concat(Enumerable.Repeat(asking, Math.Max(0, counters)));
        }

        return
        [
            new OptionalPaymentRequested(
                asked,
                context.PhysicalSourceId,
                abilityId,
                EffectIndex,
                asking)
            {
                Targets = context.Targets,
                SubjectObject = context.SubjectObject,
                YesLabel = YesLabel,
                NoLabel = NoLabel,
            },
        ];
    }
}

/// <summary>
/// Adds mana to its controller's pool (CR 106.1).
/// </summary>
/// <remarks>
/// Distinct from a mana <em>ability</em>, which does not use the stack (CR 605.3b) and is
/// modelled on <see cref="ActivatedAbilityDefinition.Produces"/>. This is for the cases that are
/// not mana abilities at all: a triggered ability that adds mana, or an ability whose mana comes
/// alongside something else. Those do use the stack, and the difference is visible — an opponent
/// can respond to one and not to the other.
/// </remarks>
public sealed record AddMana(ImmutableList<ManaProduction> Produces) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            .. Produces.Select(
                p => new ManaAdded(context.ControllerId, p.Color, p.Amount, p.Restriction)),
        ];
    }
}

/// <summary>
/// Sets up something to happen to the source at a later step (CR 603.7).
/// </summary>
/// <remarks>
/// "Sacrifice it at the beginning of the next end step" — the tail of every temporary-theft and
/// temporary-token effect there is. It creates a delayed triggered ability rather than doing
/// anything now, and that ability fires once and is gone (CR 603.7b).
/// </remarks>
public sealed record DelaySourceAction(string EffectId, State.TurnStep Step) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            new DelayedTriggerCreated(
                Guid.NewGuid(),
                context.ControllerId,
                context.PhysicalSourceId,
                Step,
                EffectId,
                context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Unearth: back from the graveyard, hasty, and exiled at end of turn (CR 702.83a).
/// </summary>
/// <remarks>
/// One effect rather than three, and that is forced rather than chosen: a card leaving the
/// graveyard becomes a new object with a new id (CR 400.7), so a second effect running afterwards
/// could not name the thing that just arrived. Only the effect that performs the move knows what
/// id it produced, so everything that has to be aimed at the returned permanent belongs here.
/// </remarks>
/// <summary>
/// Returns the source from the graveyard to the battlefield, and keeps it (CR 701.16a).
/// </summary>
/// <remarks>
/// Unearth's honest twin: the same move without the haste and without the exile at end of turn.
/// A card that recurs itself for a price is the whole of some cards, and the two differ only in
/// what happens afterwards - which is exactly why they are separate effects rather than one with
/// a flag, since forgetting to set the flag would turn one into the other silently.
/// </remarks>
public sealed record ReturnSourceToBattlefield(bool Tapped = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;
        if (!context.State.TryGetObject(sourceId, out var card) || card.Zone != Zone.Graveyard)
            return [];

        var arriving = ObjectId.New();

        // The permanent that arrives is a new object (CR 400.7), so the tap names the id the
        // move produced and never the one that was in the graveyard.
        return Tapped
            ?
            [
                new ObjectMoved(
                    sourceId, arriving, Zone.Graveyard, Zone.Battlefield, card.OwnerId,
                    MoveCause.Return),
                new PermanentTapped(arriving),
            ]
            :
            [
                new ObjectMoved(
                    sourceId, arriving, Zone.Graveyard, Zone.Battlefield, card.OwnerId,
                    MoveCause.Return),
            ];
    }
}

public sealed record UnearthSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;
        if (!context.State.TryGetObject(sourceId, out var card) || card.Zone != Zone.Graveyard)
            return [];

        var arriving = ObjectId.New();

        return
        [
            new ObjectMoved(
                sourceId, arriving, Zone.Graveyard, Zone.Battlefield, card.OwnerId,
                MoveCause.Return),
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.GrantId(KeywordAbility.Haste),
                [arriving],
                context.State.TurnNumber),
            new DelayedTriggerCreated(
                Guid.NewGuid(), context.ControllerId, arriving, State.TurnStep.End, "exile",
                context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Look at the top cards, keep one, and bury the rest (CR 701.20a).
/// </summary>
/// <remarks>
/// Records that the question is owed rather than asking it, for the same reason
/// <see cref="Scry"/> does: an effect returns events, and a question halts the whole game.
/// </remarks>
public sealed record LookAndTake(
    Amount Count,
    Zone Destination,
    Zone RestTo = Zone.Library,
    string FilterId = SearchFilters.AnyCard) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            new LookAndTakeRequested(
                context.ControllerId, Count.In(context), Destination, RestTo, FilterId),
        ];
    }
}

/// <summary>
/// Gives every permanent matching a filter a bonus until end of turn (CR 611.2).
/// </summary>
/// <remarks>
/// The set is worked out once, as it resolves, and the effect then applies to those permanents
/// for the rest of the turn — so a creature that arrives afterwards is not affected, which is
/// what "creatures get -2/-2 until end of turn" means and what a static ability would not do.
/// </remarks>
/// <param name="PeerIndex">
/// Which target of the same spell or ability the filter compares each candidate against, when it
/// does — Bile Blight's "all other creatures with the same name as that creature". Null on every
/// other group, and the group is still untargeted either way: the sibling is targeted, the
/// creatures found by looking at it are not.
/// </param>
public sealed record PumpGroup(string DefinitionId, TargetSpec What, int? PeerIndex = null)
    : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The source is handed to the filter as well, so a group phrase can say "each other
        // creature" or "each creature this is attached to" and mean it.
        var source = context.State.TryGetObject(context.PhysicalSourceId, out var self)
            ? self
            : null;

        var peer = PeerIndex is { } sibling ? context.PeerAt(sibling) : null;

        var affected = context.State.Battlefield
            .Where(id => What.Accepts(
                context.State, context.Abilities, context.State.GetObject(id),
                context.ControllerId, source, peer))
            .ToImmutableList();

        return affected.IsEmpty
            ? []
            :
            [
                new ContinuousEffectCreated(
                    Guid.NewGuid(), DefinitionId, affected, context.State.TurnNumber),
            ];
    }
}

/// <summary>What a group effect does to each permanent it finds.</summary>
public enum GroupAction
{
    /// <summary>Destroy it (CR 701.8a). Indestructible survives.</summary>
    Destroy,

    /// <summary>Destroy it with no regeneration allowed (CR 701.19c).</summary>
    DestroyNoRegeneration,

    /// <summary>Exile it (CR 701.13a). Indestructible does not help.</summary>
    Exile,

    /// <summary>Put +1/+1 counters on it (CR 121.2).</summary>
    /// <remarks>
    /// Only +1/+1 and -1/-1 are worth a group action: they are what a card puts on a whole board
    /// at once. A named counter on each of something is rare enough to stay unread rather than
    /// widen this into a kind-and-count pair that one card in the corpus would use.
    /// </remarks>
    PlusOneCounters,

    /// <summary>Put -1/-1 counters on it (CR 121.2).</summary>
    MinusOneCounters,

    /// <summary>Tap it (CR 701.26a).</summary>
    Tap,

    /// <summary>Untap it (CR 701.26b).</summary>
    Untap,

    /// <summary>Return it to its owner's hand (CR 400.7).</summary>
    ReturnToHand,

    /// <summary>Have the source deal damage to it (CR 119.3).</summary>
    Damage,
}

/// <summary>
/// Does something to every permanent matching a filter (CR 609.2).
/// </summary>
/// <remarks>
/// A sweeper targets nothing, and that is not a detail: an untargeted effect is not stopped by
/// hexproof, does not fizzle, and finds the permanents that qualify <em>as it resolves</em>
/// rather than the ones that qualified when it was cast. So it takes a filter, never target
/// indices — and one effect covers every verb, because the only thing that differs between
/// "destroy all creatures" and "exile all creatures" is the event at the end.
/// <para>
/// <paramref name="PeerIndex"/> is the one thing here that <em>is</em> about a target, and it is
/// not a contradiction: "Cleansing Beam deals 2 damage to target creature and each other creature
/// that shares a color with it" targets one creature and finds the rest by looking at it. The
/// found ones are still untargeted — hexproof does not save them and none of them can make the
/// spell fizzle.
/// </para>
/// </remarks>
/// <param name="PeerIndex">
/// Which target of the same spell or ability <see cref="TargetSpec.PeerFilter"/> compares each
/// candidate against. Null on every ordinary sweeper.
/// </param>
public sealed record ToEachPermanent(
    GroupAction Action, TargetSpec What, Amount Amount = default, int? PeerIndex = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        // Every one of the spec's filters, through the one method that asks them all. The
        // source-aware one is what "each *other* creature you control" is made of, and asking
        // only the plain one put a counter on the very permanent whose ability said to leave
        // itself out.
        var source = context.State.TryGetObject(context.PhysicalSourceId, out var self)
            ? self
            : null;

        var peer = PeerIndex is { } sibling ? context.PeerAt(sibling) : null;

        foreach (var id in context.State.Battlefield)
        {
            var obj = context.State.GetObject(id);
            if (!What.Accepts(
                context.State, context.Abilities, obj, context.ControllerId, source, peer))
            {
                continue;
            }

            var computed = Characteristics.Of(context.State, context.Abilities, obj);

            switch (Action)
            {
                case GroupAction.Destroy:
                case GroupAction.DestroyNoRegeneration:
                    // CR 702.12b: indestructible is not destroyed, and the rest still resolves.
                    if (!computed.Has(KeywordAbility.Indestructible))
                    {
                        events.Add(new ObjectMoved(
                            id,
                            ObjectId.New(),
                            Zone.Battlefield,
                            Zone.Graveyard,
                            obj.OwnerId,
                            Action is GroupAction.DestroyNoRegeneration
                                ? MoveCause.DestroyNoRegeneration
                                : MoveCause.Destroy));
                    }

                    break;

                case GroupAction.Exile:
                    events.Add(new ObjectMoved(
                        id, ObjectId.New(), Zone.Battlefield, Zone.Exile, obj.OwnerId,
                        MoveCause.Exile));
                    break;

                case GroupAction.ReturnToHand:
                    events.Add(new ObjectMoved(
                        id, ObjectId.New(), Zone.Battlefield, Zone.Hand, obj.OwnerId,
                        MoveCause.Return));
                    break;

                case GroupAction.PlusOneCounters:
                    events.Add(new CountersChanged(
                        id, CounterKinds.PlusOnePlusOne, Math.Max(1, Amount.In(context))));
                    break;

                case GroupAction.MinusOneCounters:
                    events.Add(new CountersChanged(
                        id, CounterKinds.MinusOneMinusOne, Math.Max(1, Amount.In(context))));
                    break;

                case GroupAction.Tap when obj.Permanent?.IsTapped == false:
                    events.Add(new PermanentTapped(id));
                    break;

                case GroupAction.Untap when obj.Permanent?.IsTapped == true:
                    events.Add(new PermanentsUntapped([id]));
                    break;

                case GroupAction.Damage:
                    events.Add(new DamageMarked(
                        id, Amount.In(context), computed.Has(KeywordAbility.Deathtouch),
                        context.PhysicalSourceId));
                    break;

                default:
                    break;
            }
        }

        return events;
    }
}

/// <summary>
/// Moves every matching card out of a graveyard at once (CR 400.7).
/// </summary>
/// <remarks>
/// The mass form of <see cref="MoveTargetedCard"/>, and it targets nothing: "return all creature
/// cards from your graveyard to your hand" chooses no cards, so hexproof and protection never
/// come into it and there is nothing to fizzle.
/// <para>
/// The cards are read out of the graveyard before any of them moves, because every move gives
/// its card a new identity (CR 400.7) and a list gathered as it went would lose track of itself.
/// </para>
/// </remarks>
public sealed record MoveGraveyardGroup(
    TargetSpec What, Zone Destination, PlayerScope Whose = PlayerScope.You) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        foreach (var who in PlayerScopes.Resolve(Whose, context))
        {
            foreach (var id in context.State.GetPlayer(who).Graveyard)
            {
                if (!context.State.TryGetObject(id, out var card))
                    continue;

                if (What.ObjectFilter?.Invoke(
                        context.State, context.Abilities, card, context.ControllerId) == false)
                {
                    continue;
                }

                events.Add(new ObjectMoved(
                    id, ObjectId.New(), Zone.Graveyard, Destination, who, MoveCause.Return));
            }
        }

        return events;
    }
}

/// <summary>
/// Moves a targeted card out of a graveyard (CR 400.7).
/// </summary>
/// <remarks>
/// The graveyard is a public zone (CR 404.2), so a card in it can be targeted like a permanent
/// can — which is why <see cref="TargetKind.CardInGraveyard"/> exists. Nothing produced one until
/// now, so every reanimation and every graveyard-hate card went unread despite the machinery
/// being in place.
/// </remarks>
public sealed record MoveTargetedCard(
    Zone Destination, int TargetIndex = 0, bool UnderYourControl = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.CardInGraveyard } target)
            return [];

        if (!context.State.TryGetObject(target.Subject, out var card)
            || card.Zone != Zone.Graveyard)
        {
            return [];
        }

        return
        [
            new ObjectMoved(
                target.Subject,
                ObjectId.New(),
                Zone.Graveyard,
                Destination,

                // CR 110.2a: a card put onto the battlefield enters under its owner's control
                // unless the effect says otherwise — and reanimation nearly always says
                // otherwise. Getting this wrong hands the creature back to the player it was
                // taken from, which is the opposite of what the card is for.
                UnderYourControl ? context.ControllerId : card.OwnerId,
                Destination == Zone.Exile ? MoveCause.Exile : MoveCause.Return),
        ];
    }
}

/// <summary>
/// Search your library for a card, put it somewhere, and shuffle (CR 701.23).
/// </summary>
/// <remarks>
/// Records that the search is owed rather than performing it, like a scry: which card is found is
/// the player's choice, and a choice halts the game.
/// </remarks>
/// <summary>Who does the searching, when it is not the controller.</summary>
public enum SearchWho
{
    /// <summary>The controller of the spell or ability.</summary>
    You,

    /// <summary>Whoever controls the permanent the ability is about - "its controller".</summary>
    SubjectController,
}

public sealed record SearchLibrary(
    string FilterId,
    Zone Destination,
    bool Tapped = false,
    int? MaxManaValue = null,
    int? ExactManaValue = null,
    int? MinManaValue = null,
    int Count = 1,

    /// <summary>
    /// Whose library is searched. "Its controller may search their library" is the same search
    /// pointed at somebody else, and the fetch lands under their control rather than yours.
    /// </summary>
    SearchWho Who = SearchWho.You) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // "A card named ~" - the tilde is the card's own name, and the compiler has no name to
        // put there because the parser reads a sentence and not a card. It is filled in here,
        // where the source is known, so what reaches the log names the card outright and a
        // replayed search looks for the same thing.
        var filter = FilterId;
        if (filter.Contains('~', StringComparison.Ordinal)
            && context.State.TryGetObject(context.PhysicalSourceId, out var self))
        {
            filter = filter.Replace("~", self.Card.Name, StringComparison.Ordinal);
        }

        var searcher = context.ControllerId;

        if (Who == SearchWho.SubjectController)
        {
            // The permanent the trigger was about, followed forward if it has since moved on
            // (CR 400.7) - a land that dies still names whoever controlled it.
            if (context.SubjectObject is not { } about)
                return [];

            var owner = context.State.TryGetObject(about, out var present)
                ? present
                : context.ObjectBehind?.Invoke(about);

            if (owner is null)
                return [];

            searcher = owner.ControllerId;
        }

        return
        [
            new LibrarySearchRequested(
                searcher, filter, Destination, Tapped, Count, MaxManaValue,
                MinManaValue, ExactManaValue),
        ];
    }
}

/// <summary>
/// Seek a card: one taken at random from among the cards in a library that match, put where the
/// card says, without revealing or shuffling the library.
/// </summary>
/// <remarks>
/// Seeking is a digital-only keyword action. The Comprehensive Rules do not define it and there
/// is no paragraph to cite, so none is cited here — an invented number would be worse than none,
/// because it invites the next reader to trust it.
/// <para>
/// What it <em>can</em> be defined against is <see cref="SearchLibrary"/>, which it differs from
/// in exactly two ways, both of which matter:
/// </para>
/// <list type="bullet">
/// <item><description>The card is chosen <strong>at random by the game</strong>, not by the
/// player. Reading a seek as a search would hand its controller the pick of their library, which
/// is a strictly better card than the one printed — so it goes through the game's shared seeded
/// source, like a shuffle or a discard at random, and the log carries the moves that came out
/// rather than the roll that chose them.</description></item>
/// <item><description>The library is neither revealed nor shuffled. A search shuffles afterwards
/// (CR 701.23e); leaving the order alone is most of the reason the mechanic exists at all.
/// </description></item>
/// </list>
/// <para>
/// Everything else is the search vocabulary unchanged — the same <see cref="SearchFilters"/>
/// ids, the same mana-value bounds, the same destinations — because a second filter grammar
/// would drift from this one the first time either of them learned a word.
/// </para>
/// </remarks>
public sealed record Seek(
    string FilterId,
    Zone Destination = Zone.Hand,
    bool Tapped = false,
    int Count = 1,
    int? MaxManaValue = null,
    int? MinManaValue = null,
    int? ExactManaValue = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // "A card named ~" is the card's own name, filled in here where the source is known -
        // the same substitution a search makes and for the same reason: what reaches the log has
        // to name the card outright, so a replayed seek looks for the same thing.
        var filter = FilterId;
        if (filter.Contains('~', StringComparison.Ordinal)
            && context.State.TryGetObject(context.PhysicalSourceId, out var self))
        {
            filter = filter.Replace("~", self.Card.Name, StringComparison.Ordinal);
        }

        return
        [
            new SeekRequested(
                context.ControllerId, filter, Destination, Tapped, Count,
                MaxManaValue, MinManaValue, ExactManaValue),
        ];
    }
}

/// <summary>
/// Which cards a named search filter accepts (CR 701.23).
/// </summary>
/// <remarks>
/// A closed vocabulary of names rather than a predicate, so a search survives being written to a
/// log and read back. Anything not named here leaves the line unread — a search that found the
/// wrong sort of card would be a different card entirely.
/// </remarks>
public static class SearchFilters
{
    /// <summary>Any card with a basic land type (CR 305.6).</summary>
    public const string BasicLand = "basic-land";

    /// <summary>
    /// Any card that could be a permanent - "four or more permanent cards in your graveyard".
    /// </summary>
    /// <remarks>
    /// A card type is the wrong test: "permanent" is not one (CR 205.2a), it is the set of five
    /// that make one. Without this the word fell through to the subtype check and matched
    /// nothing, because no card is printed with the subtype "permanent".
    /// </remarks>
    public const string PermanentCard = "permanent-card";

    /// <summary>Any card at all — "search your library for a card".</summary>
    public const string AnyCard = "any";

    /// <summary>
    /// A card with one exact name — "search your library for a card named Llanowar Elves".
    /// </summary>
    /// <remarks>
    /// A prefix rather than a member of the closed vocabulary, because the vocabulary is names of
    /// <em>kinds</em> and this names one card. It stays a string that survives a log, which is
    /// the whole reason filters are strings.
    /// </remarks>
    public const string NamedPrefix = "name:";

    /// <summary>Whether a card answers to a filter name.</summary>
    /// <remarks>
    /// The name is either a card type, a supertype-and-type pair, or a subtype, and they are told
    /// apart the same way everywhere else in the compiler: a capital letter means a subtype.
    /// </remarks>
    public static bool Matches(string filterId, Domain.Models.CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (string.Equals(filterId, AnyCard, StringComparison.Ordinal))
            return true;

        // "A creature or land card" is two filters and the card answers to either (CR 109.4).
        // Held as one string with a separator rather than as a list, because a filter id travels
        // in an event and has to be a value a log can carry - and because every place that reads
        // one then keeps working without knowing there are now two.
        if (filterId.Contains('|', StringComparison.Ordinal))
        {
            foreach (var one in filterId.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Matches(one, card))
                    return true;
            }

            return false;
        }

        if (filterId.StartsWith(NamedPrefix, StringComparison.Ordinal))
        {
            return string.Equals(
                card.Name,
                filterId[NamedPrefix.Length..],
                StringComparison.OrdinalIgnoreCase);
        }

        // "A noncreature, nonland card" is two filters the card must answer to *both* of, which
        // is the opposite of the bar above and needs its own separator. Written as an ampersand
        // for the same reasons the bar is a bar: it survives an event log, and every reader that
        // already handles a filter id keeps working without knowing there are now two.
        if (filterId.Contains('&', StringComparison.Ordinal))
        {
            foreach (var one in filterId.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Matches(one, card))
                    return false;
            }

            return true;
        }

        // "Noncreature" is the same question as "creature" with the answer turned round. Read
        // here rather than as its own entry per type, so a negation works anywhere a type does
        // and the two can never disagree about what a creature is.
        if (filterId.StartsWith("non", StringComparison.Ordinal))
            return !Matches(filterId[3..], card);

        if (string.Equals(filterId, PermanentCard, StringComparison.Ordinal))
        {
            return card.CardTypes.HasFlag(Domain.Enums.CardType.Artifact)
                || card.CardTypes.HasFlag(Domain.Enums.CardType.Creature)
                || card.CardTypes.HasFlag(Domain.Enums.CardType.Enchantment)
                || card.CardTypes.HasFlag(Domain.Enums.CardType.Land)
                || card.CardTypes.HasFlag(Domain.Enums.CardType.Planeswalker)
                || card.CardTypes.HasFlag(Domain.Enums.CardType.Battle);
        }

        if (string.Equals(filterId, BasicLand, StringComparison.Ordinal))
        {
            return card.Supertypes.Contains("Basic", StringComparer.OrdinalIgnoreCase)
                && card.CardTypes.HasFlag(Domain.Enums.CardType.Land);
        }

        if (CardTypeNamed(filterId) is { } type)
            return card.CardTypes.HasFlag(type);

        // Supertypes and colours, so "a basic Forest card" and "a green creature card" are two
        // filters joined rather than two phrases needing their own readers.
        if (SupertypeNamed(filterId) is { } supertype)
            return card.Supertypes.Contains(supertype, StringComparer.OrdinalIgnoreCase);

        // The card's colours (CR 202.2). Its colour identity counts the mana symbols in its
        // rules text as well (CR 903.4), which is a deck-building question and not this one.
        if (ColorNamed(filterId) is { } colour)
            return card.Colors.Contains(colour);

        // Colourless is the absence of all five rather than a sixth colour (CR 105.1), so it
        // cannot go in the table above and has to be asked as its own question.
        if (string.Equals(filterId, "colorless", StringComparison.Ordinal))
            return card.Colors.Count == 0;

        // "Multicolored" and "monocolored" count colours rather than naming one (CR 105.4), so
        // they are their own questions for the same reason colourless is. Both are printed by
        // the mana restrictions - "spend this mana only to cast a multicolored spell" - and by
        // the cost modifiers, and neither could be said with the table above.
        if (string.Equals(filterId, "multicolored", StringComparison.Ordinal))
            return card.Colors.Count > 1;

        if (string.Equals(filterId, "monocolored", StringComparison.Ordinal))
            return card.Colors.Count == 1;

        // A capitalised name is a subtype — "Forest", "Goblin", "Equipment".
        return card.Subtypes.Contains(filterId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A supertype a printed word names, or null if it is not one (CR 205.4).</summary>
    private static string? SupertypeNamed(string name) => name.ToLowerInvariant() switch
    {
        "basic" => "Basic",
        "legendary" => "Legendary",
        "snow" => "Snow",
        "world" => "World",
        _ => null,
    };

    /// <summary>A colour a printed word names, or null if it is not one (CR 105.1).</summary>
    private static Domain.Enums.ManaColor? ColorNamed(string name) => name.ToLowerInvariant() switch
    {
        "white" => Domain.Enums.ManaColor.White,
        "blue" => Domain.Enums.ManaColor.Blue,
        "black" => Domain.Enums.ManaColor.Black,
        "red" => Domain.Enums.ManaColor.Red,
        "green" => Domain.Enums.ManaColor.Green,
        _ => null,
    };

    private static Domain.Enums.CardType? CardTypeNamed(string name) => name switch
    {
        "creature" => Domain.Enums.CardType.Creature,
        "artifact" => Domain.Enums.CardType.Artifact,
        "enchantment" => Domain.Enums.CardType.Enchantment,
        "land" => Domain.Enums.CardType.Land,
        "planeswalker" => Domain.Enums.CardType.Planeswalker,
        "instant" => Domain.Enums.CardType.Instant,
        "sorcery" => Domain.Enums.CardType.Sorcery,
        _ => null,
    };
}

/// <summary>Whether a cost modifier adds to a cost or takes off it (CR 601.2f).</summary>
/// <remarks>
/// Two named values rather than a signed amount. The only cost modification the engine had could
/// physically only subtract — it accumulated a discount and called
/// <see cref="Mana.ManaCostSpec.WithoutGeneric"/> — so every "costs {1} more" on the board was
/// unread, and a sign convention smuggled into an int is exactly how the next reader gets it
/// backwards on a card that then costs less than printed.
/// </remarks>
public enum CostChange
{
    /// <summary>"...cost {1} less to cast."</summary>
    Reduction,

    /// <summary>"...cost {1} more to cast."</summary>
    Increase,
}

/// <summary>What a cost modifier modifies (CR 601.2f, 602.2b).</summary>
/// <remarks>
/// CR 602.2b makes an activated ability's activation cost the analogue of a spell's mana cost, so
/// the two are the same mechanism pointed at two different costs — which is why this is a field
/// rather than a second kind of modifier. It is also the half that did not exist: an ability's
/// cost was paid with no modifier hook at all.
/// </remarks>
public enum CostModifierKind
{
    /// <summary>"Spells you cast cost {1} less to cast."</summary>
    Spells,

    /// <summary>"Abilities you activate cost {1} less to activate."</summary>
    ActivatedAbilities,
}

/// <summary>
/// A standing change a permanent makes to what somebody's spells or abilities cost (CR 601.2f).
/// </summary>
/// <remarks>
/// Supersedes <c>CostReducer</c>, which could say only one of the six things the corpus prints:
/// it walked the caster's own battlefield and could only ever subtract. The grid is
/// (whose spells or abilities) × (more or less) × (spells or abilities), and only
/// <em>your spells, less</em> was reachable.
/// <para>
/// Deliberately not a continuous effect, for the reason <c>CostReducer</c> already gave: what a
/// spell costs is worked out once as it is cast (CR 601.2f) and never recomputed, so this is read
/// at cast time from whatever is on the battlefield at that moment rather than folded into a
/// layer.
/// </para>
/// <para>
/// A reduction comes off the generic part only, and an increase is added to it. Neither can touch
/// a coloured pip (CR 601.2f), and a cost cannot be reduced below {0}.
/// </para>
/// </remarks>
public sealed record CostModifier
{
    /// <summary>
    /// Which spells or abilities' sources it applies to, in the shared filter vocabulary.
    /// </summary>
    /// <remarks>
    /// The same names <see cref="SearchFilters"/> gives tutors and digs, so "Dragon spells",
    /// "noncreature spells" and "artifact and enchantment spells" are one filter, a negation and
    /// two filters joined, rather than three readers. For
    /// <see cref="CostModifierKind.ActivatedAbilities"/> it is asked of the permanent whose
    /// ability is being activated — "activated abilities of creatures you control".
    /// </remarks>
    public string FilterId { get; init; } = SearchFilters.AnyCard;

    /// <summary>How much generic mana it moves.</summary>
    public required int Amount { get; init; }

    /// <summary>Which way (CR 601.2f).</summary>
    public CostChange Change { get; init; } = CostChange.Reduction;

    /// <summary>Whether it modifies spells being cast or abilities being activated.</summary>
    public CostModifierKind Kind { get; init; } = CostModifierKind.Spells;

    /// <summary>
    /// Whose spells or abilities, read around whoever controls the permanent printing this.
    /// </summary>
    /// <remarks>
    /// The distinction the old reducer could not make and the one this exists for.
    /// <see cref="PlayerScope.You"/> is "spells you cast", <see cref="PlayerScope.EachOpponent"/>
    /// is "spells your opponents cast", and <see cref="PlayerScope.EachPlayer"/> is the bare
    /// "noncreature spells cost {1} more to cast", which taxes its own controller too.
    /// </remarks>
    public PlayerScope Who { get; init; } = PlayerScope.You;

    /// <summary>
    /// Whose permanent the ability has to be on, or null for anyone's.
    /// </summary>
    /// <remarks>
    /// A second scope because the cards ask two different questions and folding them would answer
    /// one of them wrongly. "Abilities <em>you activate</em> cost {1} less to activate" is about
    /// who is paying, which is <see cref="Who"/>; "activated abilities <em>of creatures you
    /// control</em> cost {2} less to activate" is about whose permanent the ability is printed on,
    /// and says nothing at all about who activates it. Only <see cref="CostModifierKind"/>'s
    /// ability half reads this: a spell has no permanent behind it.
    /// </remarks>
    public PlayerScope? SourceController { get; init; }

    /// <summary>
    /// The zone a spell has to be cast from for this to apply, or null for any (CR 400.1).
    /// </summary>
    /// <remarks>
    /// "Spells you cast from your graveyard cost {1} less to cast." The zone has to be on both
    /// halves or on neither: a reducer carrying the zone that nothing consulted would apply the
    /// reduction from every zone, which is a strictly worse card than the unread one. It is read
    /// from where the card is standing when its cost is worked out, which is before it moves to
    /// the stack.
    /// </remarks>
    public State.Zone? FromZone { get; init; }

    /// <summary>
    /// Whether mana abilities are exempt — "unless they're mana abilities" (CR 605.1a).
    /// </summary>
    public bool ExceptManaAbilities { get; init; }

    /// <summary>
    /// Whether it applies only to the abilities of the permanent that prints it.
    /// </summary>
    /// <remarks>
    /// "This ability costs {1} less to activate" is a modifier a permanent makes to itself, and
    /// reading it as "abilities you activate" would discount every other permanent's abilities
    /// too. <see cref="Who"/> is not consulted when this is set: the source's own controller is
    /// whoever is activating it.
    /// </remarks>
    public bool SourceOnly { get; init; }
}

/// <summary>
/// Where the engine finds the cost modifiers a card prints.
/// </summary>
/// <remarks>
/// A seam of its own rather than another member on <see cref="IAbilitySource"/>, so the engine
/// half can be built and tested before the compiler reads a word of it: an ability source that
/// does not implement this simply has no modifiers, which is what every source says today.
/// <para>
/// <see cref="IAbilitySource"/> should grow to extend this once the compiler emits them, at which
/// point <c>CostReducer</c> folds into <see cref="CostModifier"/> — it is exactly a
/// <see cref="CostChange.Reduction"/> of <see cref="CostModifierKind.Spells"/> scoped to
/// <see cref="PlayerScope.You"/> from any zone.
/// </para>
/// </remarks>
public interface ICostModifierSource
{
    /// <summary>What this permanent changes about somebody's costs (CR 601.2f).</summary>
    IReadOnlyList<CostModifier> CostModifiersOf(Domain.Models.CardDefinition card) => [];
}

/// <summary>Applying a set of cost modifiers to one cost (CR 601.2f).</summary>
public static class CostModification
{
    /// <summary>
    /// The cost after every modifier that applies has been taken into account.
    /// </summary>
    /// <remarks>
    /// CR 601.2f states the order and it is not the order they were found in: the total cost is
    /// the mana cost "plus all additional costs and cost increases, and minus all cost
    /// reductions". Increases first, then reductions — a {1} spell taxed {2} and discounted {2}
    /// costs {1}, where reducing first would floor at {0} and then charge {2}.
    /// <para>
    /// The mana component cannot be reduced below {0}, which
    /// <see cref="Mana.ManaCostSpec.WithoutGeneric"/> already gives us by taking off only what is
    /// there to take.
    /// </para>
    /// </remarks>
    public static Mana.ManaCostSpec Apply(
        Mana.ManaCostSpec cost, IEnumerable<CostModifier> modifiers)
    {
        ArgumentNullException.ThrowIfNull(cost);
        ArgumentNullException.ThrowIfNull(modifiers);

        var increase = 0;
        var reduction = 0;

        foreach (var modifier in modifiers)
        {
            if (modifier.Amount <= 0)
                continue;

            if (modifier.Change == CostChange.Increase)
                increase += modifier.Amount;
            else
                reduction += modifier.Amount;
        }

        return cost.PlusGeneric(increase).WithoutGeneric(reduction);
    }
}

/// <summary>
/// Does something only if the spell was kicked (CR 702.33e).
/// </summary>
/// <remarks>
/// A wrapper rather than a flag on each effect, because the card prints it as one condition over
/// a whole clause and several effects can hang off it.
/// <para>
/// CR 702.33g: a part of an ability that only happens if the spell was kicked chooses its targets
/// only if it was kicked. That is not modelled — the targets are chosen either way — which shows
/// up as a spell asking for a target it will not use. Recorded rather than hidden.
/// </para>
/// </remarks>
public sealed record IfKicked(ImmutableList<IEffect> Effects) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.State.TryGetObject(context.SourceId, out var spell) || !spell.WasKicked)
            return [];

        var events = new List<GameEvent>();
        foreach (var effect in Effects)
            events.AddRange(effect.Resolve(context));

        return events;
    }
}

/// <summary>
/// Does something, and does the rest only if the first part actually happened.
/// </summary>
/// <remarks>
/// "Tap target untapped creature you control. **If you do**, add {C} equal to its power." The
/// engine understood "if you do" only after a *may* - after an offer, where the answer is a
/// choice - and this is the other half of the phrase: after a **mandatory** action, where "if you
/// do" asks whether the action came off at all. A target that has left, a creature already tapped,
/// a card no longer in the graveyard: the instruction is given and nothing happens.
/// <para>
/// "Happened" is read as "produced events", which is the engine's own record of something having
/// occurred and is what every effect here already answers with. It is an approximation in one
/// direction only - an action that does nothing produces nothing - and it is stated rather than
/// hidden.
/// </para>
/// </remarks>
public sealed record IfItHappened(
    ImmutableList<IEffect> Doing, ImmutableList<IEffect> Then) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        foreach (var effect in Doing)
            events.AddRange(effect.Resolve(context));

        if (events.Count == 0)
            return events;

        foreach (var effect in Then)
            events.AddRange(effect.Resolve(context));

        return events;
    }
}

/// <summary>
/// Effects that happen only if the spell was bargained (CR 702.166c).
/// </summary>
/// <remarks>
/// Kicker's wrapper with a different flag, and deliberately not the same one. The two abilities
/// are linked to their own cost (CR 607.2), and a card that printed both would otherwise have
/// each half answering for the other.
/// <para>
/// <see cref="Else"/> carries the sentence the clause replaces, for the cards that say
/// "instead": "deals 3 damage to target creature. If this spell was bargained, destroy that
/// creature instead" does exactly one of the two, decided by the flag as the spell resolves.
/// Empty on the additive cards, where the clause's effects simply happen on top.
/// </para>
/// </remarks>
public sealed record IfBargained(
    ImmutableList<IEffect> Effects, ImmutableList<IEffect>? Else = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var bargained =
            context.State.TryGetObject(context.SourceId, out var spell) && spell.WasBargained;

        var events = new List<GameEvent>();
        foreach (var effect in bargained ? Effects : Else ?? [])
            events.AddRange(effect.Resolve(context));

        return events;
    }
}

/// <summary>
/// Effects that happen only if the spell was cast using teamwork (CR 702.194b).
/// </summary>
/// <remarks>
/// The bargain wrapper with teamwork's flag, kept apart for the same CR 607.2 reason: each
/// clause is linked to its own cost, and a wrapper reading another ability's flag would have
/// one half of a card answering for the other. <see cref="Else"/> is the "instead" branch,
/// exactly as it is there — and it is also how "unless this spell was cast using teamwork"
/// compiles: everything in the else arm, nothing in the main one.
/// </remarks>
public sealed record IfTeamwork(
    ImmutableList<IEffect> Effects, ImmutableList<IEffect>? Else = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var together =
            context.State.TryGetObject(context.SourceId, out var spell) && spell.WasTeamwork;

        var events = new List<GameEvent>();
        foreach (var effect in together ? Effects : Else ?? [])
            events.AddRange(effect.Resolve(context));

        return events;
    }
}

/// <summary>
/// Effects that happen only if the spell was kicked with one particular kicker cost
/// (CR 702.33f).
/// </summary>
/// <remarks>
/// <see cref="IfKicked"/> asks a yes-or-no; this asks <em>which</em>. The cost is compared by
/// its printed text because that is how the clause names it — "if it was kicked with its
/// {2}{R} kicker" — and how the payment was recorded (CR 607.2 carries the linked fact, and
/// the spelling with it).
/// </remarks>
public sealed record IfKickedWith(
    string Cost, ImmutableList<IEffect> Effects, ImmutableList<IEffect>? Else = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var paid = context.State.TryGetObject(context.SourceId, out var spell)
            && spell.KickedWith.Contains(Cost, StringComparer.OrdinalIgnoreCase);

        var events = new List<GameEvent>();
        foreach (var effect in paid ? Effects : Else ?? [])
            events.AddRange(effect.Resolve(context));

        return events;
    }
}

/// <summary>
/// The source explores (CR 701.44a).
/// </summary>
/// <remarks>
/// Three instructions that decompose entirely into things the engine already had: reveal the top
/// card; if it is a land put it in hand; otherwise put a +1/+1 counter on the explorer and let its
/// controller decide whether the card stays on top or goes to the graveyard. That last decision
/// <em>is</em> surveil (CR 701.25a) — look at one, choose top or graveyard — so it reuses the same
/// deferred question rather than introducing another kind of choice.
/// <para>
/// The library is looked at here rather than the choice being asked blind, because whether it is
/// a land decides which branch happens and the rules only offer a choice in one of them.
/// </para>
/// </remarks>
public sealed record ExploreSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var library = context.State.GetPlayer(context.ControllerId).Library;
        if (library.IsEmpty)
            return [];

        var top = context.State.GetObject(library[0]);

        if (top.Card.CardTypes.HasFlag(Domain.Enums.CardType.Land))
        {
            return
            [
                new ObjectMoved(
                    library[0], ObjectId.New(), Zone.Library, Zone.Hand,
                    context.ControllerId, MoveCause.Other),
            ];
        }

        // CR 701.44c: the counter goes on the exploring permanent, which is the source even if it
        // has since left — last known information decides who explored.
        return
        [
            new CountersChanged(context.PhysicalSourceId, CounterKinds.PlusOnePlusOne, 1),
            new LookAtTopRequested(context.ControllerId, 1, ToGraveyard: true),
        ];
    }
}

/// <summary>
/// Proliferate (CR 701.34a).
/// </summary>
/// <remarks>
/// "Choose any number of permanents and/or players with counters on them, then give each another
/// counter of each kind already there." The choosing is the whole mechanic and it is the player's,
/// so this records that the question is owed and the engine asks it — the same shape as scry.
/// </remarks>
public sealed record Proliferate : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return [new ProliferateRequested(context.ControllerId)];
    }
}

/// <summary>
/// Choose a permanent you control and move it (CR 609.4).
/// </summary>
/// <remarks>
/// "Return a land you control to its owner's hand", "sacrifice a creature" as an effect rather
/// than a cost. It is a <em>choice</em>, not a target: made on resolution, so hexproof does not
/// apply and nothing fizzles when there is nothing to pick.
/// <para>
/// <see cref="EffectIndex"/> is the locator that lets the engine find this record again when the
/// answer arrives, the same way <see cref="MayPay"/> finds its branches — the filter cannot ride
/// on the event, because a delegate is not something a log can rebuild.
/// </para>
/// </remarks>
public sealed record ChooseAndMove(
    TargetSpec What,
    Zone Destination,
    MoveCause Cause,
    int EffectIndex = 0,
    PlayerScope Scope = PlayerScope.You,
    int? TargetIndex = null,
    Zone From = Zone.Battlefield) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var abilityId = context.AbilityId
            ?? (context.State.TryGetObject(context.SourceId, out var onStack)
                ? onStack.Ability?.AbilityId
                : null);

        // "Each opponent sacrifices a creature" is one question per opponent, and each of them
        // picks from their own board — which is why the request carries who is being asked rather
        // than the effect assuming the controller. The engine asks them one at a time in the
        // order they are owed, and a player with nothing to give is skipped rather than stalled.
        // A named target replaces the scope outright, the same way a targeted discard does.
        var asked = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Player } aimed
                ? (IEnumerable<Guid>)[aimed.Player]
                : []
            : PlayerScopes.Resolve(Scope, context);

        return
        [
            .. asked.Select(who => new ChoosePermanentRequested(
                who,
                context.PhysicalSourceId,
                abilityId,
                EffectIndex,
                What.Description)),
        ];
    }
}

/// <summary>
/// Takes control of a target until end of turn (CR 613.1b).
/// </summary>
/// <remarks>
/// Its own effect rather than a named continuous effect chosen at compile time, because who takes
/// the creature is not known until the spell resolves — the same card cast by either player
/// steals it for whoever cast it.
/// </remarks>
/// <summary>
/// "Gain control of [target] for as long as you control [this]" (CR 611.2b).
/// </summary>
/// <remarks>
/// The same control effect as its until-end-of-turn sibling with a different clock: no turn
/// number, and a condition carried in the definition's id so the state stays a fold of the log.
/// The condition names the source as it is *now* - a permanent that leaves and returns is a new
/// object (CR 400.7), the old id names nothing, and the effect ends, which is what the card says.
/// </remarks>
/// <summary>
/// "[Target] gains protection from the color of your choice until end of turn" (CR 202.2).
/// </summary>
/// <remarks>
/// The colour is not known when the effect resolves, so the effect cannot finish on its own: it
/// asks, and the answer builds the grant. That is the same shape as every other question the
/// game stops for, and the request carries the permanents it is about so nothing has to be
/// looked up again once the answer arrives.
/// </remarks>
/// <summary>
/// "Amass Orcs 2" — grow an Army, making one first if you have none (CR 701.44a).
/// </summary>
/// <remarks>
/// Two steps that have to happen in order and in one resolution: the token is created *then* the
/// counters go on it, so a player with no Army ends with a 2/2 rather than with a 0/0 that dies
/// to state-based actions before anything can grow it.
/// <para>
/// The token is created here and the counters are asked for separately, which works because the
/// request carries the candidates it was built with - the new Army among them - rather than
/// recomputing the set after the token has arrived.
/// </para>
/// </remarks>
public sealed record Amass(string Kind, Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var you = context.ControllerId;
        var many = Count.In(context);
        if (many <= 0)
            return [];

        var armies = context.State.Battlefield
            .Select(context.State.GetObject)
            .Where(o => Characteristics.Of(context.State, context.Abilities, o).ControllerId == you
                && o.Card.Subtypes.Contains("Army", StringComparer.OrdinalIgnoreCase))
            .Select(o => o.Id)
            .ToImmutableList();

        if (!armies.IsEmpty)
        {
            return [new CounterChoiceRequested(
                you, armies, CounterKinds.PlusOnePlusOne, many)];
        }

        // CR 701.44b: no Army means one is created first, and it is a 0/0 - it only survives
        // because the counters arrive in the same resolution, before state-based actions run.
        var token = new Domain.Models.CardDefinition
        {
            OracleId = "token-army-" + Kind.ToLowerInvariant(),
            Name = Kind + " Army",
            CardTypes = Domain.Enums.CardType.Creature | Domain.Enums.CardType.Token,
            Subtypes = [Kind, "Army"],
            Power = 0,
            Toughness = 0,
            Colors = [Domain.Enums.ManaColor.Black],
            ColorIdentity = [Domain.Enums.ManaColor.Black],
        };

        var made = ObjectId.New();

        return
        [
            new ObjectCreated(made, token, you, you, Zone.Battlefield),
            new CountersChanged(made, CounterKinds.PlusOnePlusOne, many),
        ];
    }
}

/// <summary>
/// "Bolster N" — counters on the smallest creature you control (CR 701.36a).
/// </summary>
/// <remarks>
/// The candidate set is the whole of the keyword: creatures you control tied for the least
/// toughness. With one that is not a choice and the counters simply land; with a tie the rules
/// say the player chooses, and the ask is what says so.
/// <para>
/// Toughness is the computed one, not the printed one - a 1/1 under an anthem is not the
/// smallest creature on a board with a printed 2/2 (CR 613).
/// </para>
/// </remarks>
/// <summary>"Blight N" - N -1/-1 counters on a creature you choose (CR 701.68a).</summary>
/// <remarks>
/// Bolster's mirror, and it borrows bolster's machinery entirely: the same request asking which
/// of a set of candidates the counters go on. The two differ in the set - bolster is forced onto
/// the least tough and this is free - and in which counter, and in nothing else.
/// </remarks>
public sealed record Blight(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var you = context.ControllerId;

        var mine = context.State.Battlefield
            .Select(context.State.GetObject)
            .Select(o => (Object: o, Computed: Characteristics.Of(context.State, context.Abilities, o)))
            .Where(pair => pair.Computed.IsCreature && pair.Computed.ControllerId == you)
            .Select(pair => pair.Object)
            .Select(o => o.Id)
            .ToImmutableList();

        var many = Count.In(context);

        return mine.IsEmpty || many <= 0
            ? []
            : [new CounterChoiceRequested(you, mine, CounterKinds.MinusOneMinusOne, many)];
    }
}

public sealed record Bolster(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var you = context.ControllerId;

        var mine = context.State.Battlefield
            .Select(context.State.GetObject)
            .Select(o => (Object: o, Computed: Characteristics.Of(context.State, context.Abilities, o)))
            .Where(pair => pair.Computed.IsCreature && pair.Computed.ControllerId == you)
            .ToList();

        if (mine.Count == 0)
            return [];

        var least = mine.Min(pair => pair.Computed.Toughness ?? 0);

        var smallest = mine
            .Where(pair => (pair.Computed.Toughness ?? 0) == least)
            .Select(pair => pair.Object.Id)
            .ToImmutableList();

        var many = Count.In(context);

        return many <= 0
            ? []
            : [new CounterChoiceRequested(
                you, smallest, CounterKinds.PlusOnePlusOne, many)];
    }
}

/// <summary>
/// "Look at the top four cards of your library, then put them back in any order" (CR 701.19a).
/// </summary>
/// <remarks>
/// Looking is what makes the arrangement meaningful, and both halves are one instruction: the
/// cards never leave the library, so nothing moves and nothing changes identity. The order is
/// stated once, when the player has answered.
/// </remarks>
public sealed record LookAtTopThenArrange(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var many = Count.In(context);
        if (many <= 0)
            return [];

        var top = context.State.GetPlayer(context.ControllerId).Library
            .Take(many)
            .ToImmutableList();

        // Fewer cards than the card names is not an error - a library can be short - and one card
        // has only one order, so neither is worth stopping the game for.
        return top.Count < 2
            ? []
            : [new LibraryOrderRequested(context.ControllerId, top)];
    }
}

/// <summary>
/// "Untap up to three lands" — the controller picks, as it resolves (CR 701.21a).
/// </summary>
/// <remarks>
/// No target is named, so the candidates are whatever is tapped when the effect resolves rather
/// than what was tapped when it was cast. The filter is the one every other group phrase uses, so
/// "lands" and "artifacts you control" mean here exactly what they mean everywhere else.
/// </remarks>
public sealed record UntapUpTo(
    Amount Most,
    Func<GameState, GameObject, Guid, bool> Candidate) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var you = context.ControllerId;

        var tapped = context.State.Battlefield
            .Select(context.State.GetObject)
            .Where(o => o.Permanent?.IsTapped == true && Candidate(context.State, o, you))
            .Select(o => o.Id)
            .ToImmutableList();

        // Nothing tapped is not a question: an effect that stopped the game to offer an empty
        // list would be a hang rather than a choice.
        var most = Most.In(context);

        return tapped.IsEmpty || most <= 0
            ? []
            : [new UntapChoiceRequested(you, tapped, most)];
    }
}

public sealed record ChooseColorForTarget(
    ColorChoiceUse Use,
    int? TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // No index means the source itself - "~ becomes the color of your choice" rather than
        // "target permanent becomes ...". The two sentences differ only in what they aim at, so
        // they share the effect rather than having one each.
        var subject = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Permanent } target
                ? target.Subject
                : (ObjectId?)null
            : context.PhysicalSourceId;

        if (subject is not { } chosen)
            return [];

        // A subject that has left is skipped rather than asked about (CR 608.2b): choosing a
        // colour for a permanent that is no longer there would stop the game for nothing.
        if (!context.State.TryGetObject(chosen, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new ColorChoiceRequested(
                context.ControllerId, context.PhysicalSourceId, [chosen], Use),
        ];
    }
}

/// <summary>
/// "Target creature blocks ~ this turn if able" - provoke and its relatives (CR 509.1a).
/// </summary>
/// <remarks>
/// The attacker is the source and is only known while the ability resolves, so the id is built
/// here rather than at compile time the way a fixed requirement's is. That is the whole reason
/// this is an effect of its own and not another <see cref="PumpUntilEndOfTurn"/>.
/// </remarks>
/// <summary>"Target creature can't block ~ this turn" (CR 509.1b).</summary>
/// <remarks>
/// The mirror of <see cref="MustBlockSource"/>, and an effect of its own for the same reason:
/// the attacker it names is the source, known only while the ability resolves.
/// </remarks>
/// <summary>Suspects a permanent - menace and no blocking, indefinitely (CR 701.60c).</summary>
/// <remarks>
/// "Until it leaves the battlefield" needs no expiry of its own: a permanent that leaves is a
/// new object (CR 400.7), so an effect keyed to the old id applies to nothing from that moment.
/// The designation is not an ability and not copiable (CR 701.60b), which is why it is granted
/// as a continuous effect on the object rather than written onto the card.
/// </remarks>
/// <summary>Goads a creature until the goader's next turn (CR 701.15a).</summary>
/// <remarks>
/// The goader is the ability's controller and the turn is now, and both have to travel with the
/// effect - it carries no state of its own and the requirement has to outlive the resolution
/// that made it.
/// </remarks>
public sealed record GoadTarget(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.GoadedById(
                    context.ControllerId, context.State.TurnNumber),
                [target.Subject],

                // The duration is the While predicate on the definition, not a turn number: it
                // ends on somebody's next turn rather than at the end of this one.
                UntilEndOfTurn: null),
        ];
    }
}

public sealed record SuspectTarget(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.GrantId(
                    KeywordAbility.Menace | KeywordAbility.CantBlock),
                [target.Subject],
                UntilEndOfTurn: null),
        ];
    }
}

public sealed record CantBlockSource(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var source)
            || source.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.CantBlockAttackerId(source.Id),
                [target.Subject],
                context.State.TurnNumber),
        ];
    }
}

public sealed record MustBlockSource(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        // A requirement to block something no longer on the battlefield is no requirement, and
        // the id would name nothing.
        if (!context.State.TryGetObject(context.PhysicalSourceId, out var source)
            || source.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.MustBlockAttackerId(source.Id),
                [target.Subject],
                context.State.TurnNumber),
        ];
    }
}

public sealed record GainControlWhileSourceHolds(
    Cards.GenerativeEffects.ControlHeldWhile Until,
    int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        // The source has to be a permanent for the condition to mean anything. An ability whose
        // source has already left takes nothing rather than taking it for ever.
        if (!context.State.TryGetObject(context.PhysicalSourceId, out var source)
            || source.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.ControlWhileId(context.ControllerId, source.Id, Until),
                [target.Subject],
                UntilEndOfTurn: null),
        ];
    }
}

/// <summary>
/// Puts a named continuous effect on a target that lasts "for as long as …" (CR 611.2b).
/// </summary>
/// <remarks>
/// <see cref="PumpUntilEndOfTurn"/> with the other kind of duration, and the only difference is
/// which one: that one is ended by the turn number in the cleanup step, and this one by a
/// condition the definition carries. Folding the condition into the effect's own <c>Applies</c>
/// instead would be a different card — CR 611.2b says an effect whose duration ends is over and
/// does not start again, so a creature pumped "for as long as this artifact remains tapped" would
/// otherwise get its bonus back every time the artifact was tapped again.
/// <para>
/// The source has to be a permanent for the condition to mean anything, and an ability whose
/// source has already left does nothing rather than doing it for ever. That is the same guard
/// <see cref="GainControlWhileSourceHolds"/> makes, and for the same reason: the id names an
/// object, and an object that is gone is a different one (CR 400.7).
/// </para>
/// </remarks>
public sealed record HoldsWhileSourceHolds(
    string DefinitionId,
    Cards.GenerativeEffects.ControlHeldWhile Until,
    int TargetIndex = 0,
    EffectSubject Subject = EffectSubject.Target) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } affected)
            return [];

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var source)
            || source.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.HeldWhileId(
                    DefinitionId, context.ControllerId, source.Id, Until),
                [affected],
                UntilEndOfTurn: null),
        ];
    }
}

public sealed record GainControlUntilEndOfTurn(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.ControlId(context.ControllerId),
                [target.Subject],
                context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Flip a coin, and do one thing or the other (CR 705.2).
/// </summary>
/// <remarks>
/// Deferred like a question, though nobody is being asked: an effect returns events and cannot
/// reach the randomness, which lives on the game so that every random outcome in a match comes
/// from one seeded source and lands in the log. The engine flips at the next settle and runs the
/// branch the coin names.
/// </remarks>
/// <summary>"Clash with an opponent. If you win, ..." (CR 701.30b).</summary>
/// <remarks>
/// The coin flip's sibling, and it carries its branch the same way - through a locator back into
/// the card, because effects cannot travel in a log. What it does not share is the middle: a
/// clash reveals two cards and lets both players decide where theirs goes <em>before</em> the
/// winner matters, and those decisions change what a winner's "draw a card" draws.
/// </remarks>
public sealed record Clash(ImmutableList<IEffect> IfWon, int EffectIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var abilityId = context.AbilityId
            ?? (context.State.TryGetObject(context.SourceId, out var onStack)
                ? onStack.Ability?.AbilityId
                : null);

        return
        [
            new ClashRequested(
                context.ControllerId, context.PhysicalSourceId, abilityId, EffectIndex)
            {
                SubjectObject = context.SubjectObject,
            },
        ];
    }
}

public sealed record FlipCoin(
    ImmutableList<IEffect> IfWon, ImmutableList<IEffect> IfLost, int EffectIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var abilityId = context.AbilityId
            ?? (context.State.TryGetObject(context.SourceId, out var onStack)
                ? onStack.Ability?.AbilityId
                : null);

        return
        [
            new CoinFlipRequested(
                context.ControllerId, context.PhysicalSourceId, abilityId, EffectIndex)
            {
                SubjectObject = context.SubjectObject,
            },
        ];
    }
}

/// <summary>Makes a permanent monstrous, with the counters that come with it (CR 701.32a).</summary>
/// <remarks>
/// One effect and not two, because the rule is one action: a creature that is already monstrous
/// gets neither the counters nor the designation, and splitting them would let a second
/// activation put counters on without the condition ever being asked.
/// </remarks>
/// <summary>
/// Runs an add-mana effect and keeps exactly what it produced through the emptying (CR 500.4).
/// </summary>
/// <remarks>
/// A wrapper rather than a variant of each add-mana effect, because the sentence before this one
/// takes a dozen different shapes - a fixed string of symbols, one per artifact you control, "that
/// much mana of any one color" - and every one of them ends with the same clause. Wrapping reads
/// all of them at once; fusing would need a persistent twin of each.
/// <para>
/// It reads its own inner effect's events to learn the amount, which is the only place that
/// number exists: the pool at this moment may also hold mana from elsewhere, and keeping the pool
/// would hand the player mana this ability never made.
/// </para>
/// </remarks>
public sealed record KeepManaAdded(IEffect Inner, Mana.ManaPersistence Until) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var produced = Inner.Resolve(context);

        var added = Mana.ManaPool.Empty;
        foreach (var e in produced.OfType<ManaAdded>())
        {
            if (e.PlayerId != context.ControllerId)
                continue;

            added = e.Color is { } color
                ? added.Add(color, e.Amount)
                : added.AddColorless(e.Amount);
        }

        if (added.IsEmpty)
            return produced;

        return [.. produced, new ManaMadePersistent(context.ControllerId, added, Until)];
    }
}

/// <summary>
/// "The owner of target nonland permanent puts it on their choice of the top or bottom of their
/// library."
/// </summary>
/// <remarks>
/// The choice belongs to the owner rather than to whoever cast this, which is the whole reason
/// it is asked at all: a card that let the caster decide would be a strictly better removal
/// spell than the one printed.
/// </remarks>
public sealed record OwnerChoosesLibraryEnd(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target
            || !context.State.TryGetObject(target.Subject, out var doomed)
            || doomed.Zone != Zone.Battlefield)
        {
            return [];
        }

        return [new LibraryEndChoiceRequested(doomed.OwnerId, doomed.Id)];
    }
}

/// <summary>"Cipher" - the spell may exile itself encoded on a creature (CR 702.99a).</summary>
public sealed record CipherSelf : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [new CipherRequested(context.SourceId, context.ControllerId)];
    }
}

/// <summary>
/// "Exile ~ with three time counters on it" - the spell suspends itself as it finishes.
/// </summary>
/// <remarks>
/// Every printing of this also carries suspend, so the ticking and the free cast at the end are
/// already on the card: what was missing is only the exile that starts the clock. Asked for
/// rather than performed, because the card is still the spell on the stack right now and the
/// counters have to land on the object it becomes (CR 400.7).
/// </remarks>
public sealed record SuspendSourceOnResolve(int Counters) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [new SuspendOnResolveRequested(context.SourceId, Counters)];
    }
}

/// <summary>
/// "You may put a land card from your hand onto the battlefield" - not a land drop (CR 305.1).
/// </summary>
/// <remarks>
/// The same choice machinery an opponent's discard uses, pointed at the controller's own hand and
/// allowed to decline. It is deliberately not a land play: a land put onto the battlefield this
/// way does not use the turn's one land drop, and reading it as a play would be a strictly worse
/// card on every turn the player had not yet played a land.
/// </remarks>
public sealed record PutFromHandOntoBattlefield(string FilterId, bool Tapped = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            new HandChoiceRequested(
                context.ControllerId,
                context.ControllerId,
                context.PhysicalSourceId,
                context.AbilityId,
                EffectIndex: 0,
                string.Equals(FilterId, SearchFilters.AnyCard, StringComparison.Ordinal)
                    ? "a card"
                    : $"a {FilterId} card",
                FilterId,
                Zone.Battlefield,
                Optional: true,
                Tapped),
        ];
    }
}

/// <summary>
/// "When ~ enters, sacrifice it unless you [pay a cost that is not mana]."
/// </summary>
/// <remarks>
/// The same offer <see cref="MayPay"/> makes, with the "if you don't" branch fixed: this
/// permanent goes. It was its own effect because the offer could only charge mana, life and
/// energy; now that the offer can charge a selection too, the two ask the same question and
/// share the answering — <c>Game.PayableFor</c> lists what may be given up and
/// <c>Game.TakeChosenPayment</c> moves it, for both. What stays separate is only what the two
/// requests carry: this one names the permanent at stake, so the question can say what declining
/// costs, and that is a thing to say rather than a branch to run.
/// </remarks>
public sealed record SacrificeSourceUnlessPaid(
    ChosenCostKind Kind, int Count, string FilterId) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The permanent has to still be there to be sacrificed, and it is the permanent rather
        // than the trigger on the stack - the trigger's source id is the object that entered.
        if (!context.State.TryGetObject(context.PhysicalSourceId, out var self)
            || self.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new SacrificeUnlessPaidRequested(
                context.ControllerId, self.Id, Kind, Count, FilterId),
        ];
    }
}

/// <summary>"Take an extra turn after this one" (CR 500.7).</summary>
/// <remarks>
/// The subject is a target when the card names one and the controller otherwise, which is the
/// same choice every other player-aimed effect makes. It is queued rather than taken: the current
/// turn finishes first, and the rule is explicit that the extra one comes after it.
/// </remarks>
public sealed record TakeExtraTurn(int? TargetIndex = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (TargetIndex is not { } index)
            return [new ExtraTurnCreated(context.ControllerId)];

        return context.TargetAt(index) is { Kind: TargetKind.Player } chosen
            && context.State.Players.ContainsKey(chosen.Player)
            ? [new ExtraTurnCreated(chosen.Player)]
            : [];
    }
}

/// <summary>Lets the controller play extra lands for the rest of this turn (CR 505.6b).</summary>
public sealed record GrantExtraLandDrop(int Count = 1) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [new ExtraLandDropGranted(context.ControllerId, Count)];
    }
}

/// <summary>Makes the source permanent prepared, so its spell is available to copy.</summary>
/// <remarks>
/// The subject is always the source: every printing says "it", "this creature" or the card's own
/// name, and each of those is the permanent the ability is printed on. A permanent that is
/// already prepared is unchanged rather than doubly so - the designation is a fact about the
/// permanent, not a resource that stacks.
/// </remarks>
public sealed record PrepareSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var self)
            || self.Permanent is not { IsPrepared: false }
            || context.Abilities.PreparedSpellOf(self.Card) is null)
        {
            return [];
        }

        return [new BecamePrepared(self.Id)];
    }
}

public sealed record Monstrosity(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var self)
            || self.Permanent is not { IsMonstrous: false })
        {
            return [];
        }

        return
        [
            new CountersChanged(self.Id, CounterKinds.PlusOnePlusOne, Count.In(context)),
            new BecameMonstrous(self.Id),
        ];
    }
}

/// <summary>
/// Does something only while a condition holds (CR 603.4).
/// </summary>
/// <remarks>
/// The intervening-if clause. CR 603.4 checks it twice — once when the ability would trigger, and
/// again as it resolves — so the trigger predicate carries the same condition and this is the
/// second half. A creature that was there when the upkeep began and has since died stops the
/// ability doing anything, which is the whole reason the rule checks twice.
/// <para>
/// The predicate is a delegate, which nothing in the state holds: effects live in the compiled
/// card definition and are rebuilt from the card, exactly like a trigger's own condition.
/// </para>
/// </remarks>
public sealed record OnlyIf(
    Func<GameState, IAbilitySource, GameObject, bool> Condition,
    ImmutableList<IEffect> Effects) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var source)
            || !Condition(context.State, context.Abilities, source))
        {
            return [];
        }

        var events = new List<GameEvent>();
        foreach (var effect in Effects)
            events.AddRange(effect.Resolve(context));

        return events;
    }
}

/// <summary>
/// Binds X to a count for the sentence that defined it — "..., where X is the number of ...".
/// </summary>
/// <remarks>
/// X is ordinarily a number the caster chose (CR 601.2b), and every effect that uses one reads it
/// off the resolution context. A sentence that <em>defines</em> X instead is not asking for a
/// different kind of amount; it is answering the same question a different way. So this wraps the
/// sentence and fills the answer in, and every effect inside keeps reading X exactly as it did.
/// <para>
/// Wrapping rather than rewriting the amounts is the point. The alternative is walking the parsed
/// effects and replacing every variable amount inside them, which means knowing the shape of each
/// one — and there are dozens, some nested. This knows nothing about them.
/// </para>
/// <para>
/// The count is taken when the sentence resolves, not when the spell was cast: "where X is the
/// number of creatures you control" on a spell that killed a creature earlier in its own text
/// counts what is left, which is what the card says.
/// </para>
/// </remarks>
public sealed record WithCountedVariable(
    Func<ResolutionContext, int> Count, ImmutableList<IEffect> Effects) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var bound = context with { VariableValue = Count(context) };

        var events = new List<GameEvent>();
        foreach (var effect in Effects)
            events.AddRange(effect.Resolve(bound));

        return events;
    }
}

/// <summary>
/// Pumps a target by an amount that is not known until the ability resolves (CR 613.4c).
/// </summary>
/// <remarks>
/// Every other pump names a continuous effect that was written when the card compiled, because the
/// size was on the card. "+X/+X" has no size until X does, so the definition is *built* here from
/// the amount and handed over by the same event — the layer machinery is unchanged and never
/// learns that a pump can be variable.
/// <para>
/// Power and toughness are separate amounts rather than one, because the cards separate them:
/// "+X/+0" is far commoner than "+X/+X" and reads the same X twice only by coincidence of
/// wording.
/// </para>
/// </remarks>
public sealed record PumpTargetByVariable(
    Amount Power, Amount Toughness, int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.PumpId(Power.In(context), Toughness.In(context)),
                [target.Subject],
                context.State.TurnNumber),
        ];
    }
}

/// <summary>Untaps the source itself (CR 701.26b).</summary>
public sealed record UntapSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;

        return context.State.TryGetObject(sourceId, out var permanent)
            && permanent.Permanent?.IsTapped == true
                ? [new PermanentsUntapped([sourceId])]
                : [];
    }
}

/// <summary>Ascend - ten permanents buys the city's blessing (CR 702.131a).</summary>
/// <remarks>
/// The count is of <em>permanents</em>, not of creatures and not of cards: lands and the
/// enchantment asking the question are all in it, which is what makes ten reachable at all.
/// <para>
/// Doing nothing when the player already has it is the rule rather than an optimisation - the
/// ability reads "and you don't have the city's blessing" - and it is also what keeps a state
/// trigger from firing forever, since the condition it watches stays true once ten permanents
/// are out.
/// </para>
/// </remarks>
public sealed record GainCitysBlessing : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.State.GetPlayer(context.ControllerId).HasCitysBlessing)
            return [];

        var permanents = context.State.Battlefield.Count(
            id => context.State.TryGetObject(id, out var permanent)
                && permanent.ControllerId == context.ControllerId);

        return permanents >= 10 ? [new CitysBlessingGained(context.ControllerId)] : [];
    }
}

/// <summary>
/// The controller becomes the monarch (CR 725.3).
/// </summary>
/// <remarks>
/// Nothing to check and nothing to refuse: a player who is already the monarch becoming it again
/// is a no-op the state handles, and the previous monarch stops being one by the same event.
/// Unlike the city's blessing this is not one-way - the whole point of the designation is that it
/// moves, and it moves most often because somebody hit its holder in combat.
/// </remarks>
public sealed record BecomeTheMonarch : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.State.MonarchId == context.ControllerId
            ? []
            : [new MonarchChanged(context.ControllerId)];
    }
}

/// <summary>
/// The creature this ability belongs to becomes renowned (CR 702.112b).
/// </summary>
public sealed record BecomeRenowned : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        return context.State.TryGetObject(subject, out var hero)
            && hero.Permanent is { IsRenowned: false }
            ? [new BecameRenowned(subject)]
            : [];
    }
}

/// <summary>
/// The Case this ability belongs to becomes solved (CR 719.3a).
/// </summary>
/// <remarks>
/// The permanent behind the ability, not the ability on the stack - the same distinction every
/// other self-affecting effect here draws.
/// </remarks>
public sealed record SolveCase : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        return context.State.TryGetObject(subject, out var investigation)
            && investigation.Permanent is { IsSolved: false }
            ? [new CaseSolved(subject)]
            : [];
    }
}

/// <summary>
/// The Class this ability belongs to becomes the given level (CR 716.2a).
/// </summary>
/// <remarks>
/// A level bar sets the level rather than raising it by one. The two are the same thing while
/// the bars are activated in order - which the activation condition enforces - and writing it as
/// "gain a level" would quietly disagree with the card the moment anything else set a level.
/// <para>
/// The permanent behind the ability, not the ability on the stack: an ability is its own object
/// while it resolves, and the level belongs to the enchantment.
/// </para>
/// </remarks>
public sealed record GainClassLevel(int Level) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        return context.State.TryGetObject(subject, out var klass) && klass.Permanent is not null
            ? [new ClassLevelChanged(subject, Level)]
            : [];
    }
}

/// <summary>
/// The Mount this ability belongs to becomes saddled (CR 702.171a).
/// </summary>
/// <remarks>
/// The permanent behind the ability rather than the ability on the stack, which is the same trap
/// <see cref="TransformSource"/> fell into: an ability is its own object while it resolves.
/// </remarks>
public sealed record SaddleSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        return context.State.TryGetObject(subject, out var mount) && mount.Permanent is not null
            ? [new PermanentSaddled(subject)]
            : [];
    }
}

/// <summary>
/// Turns the source to its other face (CR 701.28, "transform").
/// </summary>
/// <remarks>
/// Only a permanent with another face can transform, and a permanent already showing the face it
/// is told to show does nothing - both are silent no-ops rather than errors, because the rules
/// treat an impossible transform as simply not happening (CR 712.9).
/// </remarks>
public sealed record TransformSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The permanent behind the ability, not the ability on the stack. A triggered ability is
        // its own object while it is resolving, and asking that object for its faces finds none -
        // which is how the first version of this fired, resolved and turned nothing over.
        var subject = context.PhysicalSourceId;

        if (!context.State.TryGetObject(subject, out var permanent)
            || permanent.Permanent is not { } onBattlefield
            || permanent.Card.Faces.Count < 2)
        {
            return [];
        }

        var next = onBattlefield.FaceIndex == 0 ? 1 : 0;
        return [new PermanentTransformed(subject, next)];
    }
}

/// <summary>Sacrifices the source itself (CR 701.21).</summary>
/// <summary>
/// Exiles the permanent this ability belongs to (CR 701.13a).
/// </summary>
/// <remarks>
/// The twin of <see cref="SacrificeSource"/>, and it is a separate effect rather than a flag on
/// one because the two are different actions: a sacrifice can be watched for and an exile cannot,
/// and nothing that triggers on a permanent being sacrificed should fire for this.
/// </remarks>
public sealed record ExileSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;

        if (!context.State.TryGetObject(sourceId, out var permanent)
            || permanent.Zone is Zone.Exile)
        {
            return [];
        }

        return
        [
            new ObjectMoved(
                sourceId, ObjectId.New(), permanent.Zone, Zone.Exile, permanent.OwnerId,
                MoveCause.Exile),
        ];
    }
}

public sealed record SacrificeSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;

        if (!context.State.TryGetObject(sourceId, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new ObjectMoved(
                sourceId, ObjectId.New(), Zone.Battlefield, Zone.Graveyard, permanent.OwnerId,
                MoveCause.Sacrifice),
        ];
    }
}

/// <summary>Creates a token under the controller's control (CR 111.1).</summary>
public sealed record CreateToken(
    Domain.Models.CardDefinition Token,
    Amount Count = default,
    bool Tapped = false) : IEffect
{
    /// <summary>Who gets the tokens, when the sentence names somebody other than you.</summary>
    /// <remarks>
    /// A token is created under a player's control (CR 111.1), and which player is a question
    /// the sentence answers — "each opponent creates a Treasure token" gives them one each.
    /// </remarks>
    public PlayerScope Scope { get; init; } = PlayerScope.You;

    /// <summary>A named player, when the sentence targets one rather than naming a group.</summary>
    public int? TargetIndex { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // "Create a token" with no number means one, and a default Amount is zero — so an unset
        // count becomes one. A count that was given is used as it stands, including when it
        // counts to nothing: "a token for each artifact you control" with no artifacts makes no
        // tokens, and flooring that at one would be a card doing something it does not say.
        var count = Count.Equals(default(Amount)) ? 1 : Math.Max(0, Count.In(context));

        // A named target wins outright over a scope, the same way it does for drawing, life and
        // discarding: the sentence named one player and the scope named none.
        var owners = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Player } aimed
                ? (IEnumerable<Guid>)[aimed.Player]
                : []
            : PlayerScopes.Resolve(Scope, context);

        var made = new List<GameEvent>();

        foreach (var owner in owners)
        {
            foreach (var _ in Enumerable.Range(0, count))
            {
                var id = ObjectId.New();

                made.Add(new ObjectCreated(id, Token, owner, owner, Zone.Battlefield));

                if (Tapped)
                    made.Add(new PermanentTapped(id));
            }
        }

        return made;
    }
}
