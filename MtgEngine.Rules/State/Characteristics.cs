using System.Collections.Immutable;
using MtgEngine.Domain.Enums;
using MtgEngine.Rules.Abilities;

namespace MtgEngine.Rules.State;

/// <summary>An object's characteristics after every continuous effect has been applied.</summary>
public sealed record ComputedCharacteristics
{
    public int? Power { get; init; }

    public int? Toughness { get; init; }

    public CardType CardTypes { get; init; }

    public KeywordAbility Keywords { get; init; }

    public Guid ControllerId { get; init; }

    public ImmutableList<string> Subtypes { get; init; } = [];

    public ImmutableList<ManaColor> Colors { get; init; } = [];

    public bool IsCreature => CardTypes.HasFlag(CardType.Creature);

    public bool Has(KeywordAbility keyword) => Keywords.HasFlag(keyword);

    /// <summary>CR 702.73a: whether this is every creature type (changeling).</summary>
    public bool IsEveryCreatureType { get; init; }

    /// <summary>
    /// Whether this permanent skips its controller's untap step (CR 502.3).
    /// </summary>
    /// <remarks>
    /// A restriction rather than an ability, so it is not a keyword and cannot be granted by
    /// flag. It is computed with everything else because the commonest way to get it is from
    /// somewhere other than the card — an Aura that says "enchanted creature doesn't untap" is
    /// one permanent putting the restriction on another, and only the layers know that.
    /// </remarks>
    public bool DoesNotUntap { get; init; }

    /// <summary>
    /// Activated abilities an effect gave this object, on top of its printed ones (CR 613.1f).
    /// </summary>
    /// <remarks>
    /// Layer 6 adds <em>abilities</em>, not only keywords, and the keyword flags could not hold
    /// one: an Aura reading <c>Enchanted land has "{T}: Add {B}"</c> grants a whole activated
    /// ability to one particular permanent, which is neither a flag nor a property of the card.
    /// Anything asking what a permanent can do has to ask here as well as asking the card, which
    /// is what <see cref="Engine.Game.ActivatedAbilitiesOf"/> is for.
    /// </remarks>
    public ImmutableList<ActivatedAbilityDefinition> GrantedActivated { get; init; } = [];

    /// <summary>Triggered abilities this object was given in layer 6 (CR 613.1f).</summary>
    public ImmutableList<TriggeredAbilityDefinition> GrantedTriggers { get; init; } = [];

    /// <summary>
    /// Which creatures may not block this one (CR 509.1b).
    /// </summary>
    /// <remarks>
    /// Each entry is asked (attacker, blocker) and answers whether that block is allowed. It is a
    /// list rather than one predicate because a creature can collect restrictions from several
    /// places at once — its own text, an Aura, a lord — and they all apply together.
    /// <para>
    /// Computed rather than read off the card, because the commonest source of one is somewhere
    /// else: "enchanted creature can't be blocked by creatures with flying" is a restriction on a
    /// permanent whose own text says nothing about blocking.
    /// </para>
    /// </remarks>
    public ImmutableList<BlockRestriction> BlockRestrictions { get; init; } = [];

    /// <summary>
    /// How many creatures beyond the first this one may block (CR 509.1a).
    /// </summary>
    /// <remarks>
    /// A number rather than a flag because the grants stack: two effects each saying "an
    /// additional creature" let it block three, and a card that says "any number" is the same
    /// characteristic with a very large one rather than a second mechanism.
    /// </remarks>
    public int ExtraBlocks { get; init; }

    /// <summary>The fewest creatures that may block this one, as menace generalises (CR 509.1b).</summary>
    public int MinBlockers { get; init; }

    /// <summary>
    /// Whether every creature able to block this one has to (CR 509.1c) - a lure.
    /// </summary>
    /// <remarks>
    /// A requirement rather than a restriction, which is the opposite direction: a restriction
    /// says a block may not happen, and this says one must. The two are checked in the same
    /// place because the rule resolves them together - a requirement is only binding while no
    /// restriction contradicts it.
    /// </remarks>
    public bool MustBeBlockedByAll { get; init; }

    /// <summary>Whether at least one creature able to block this one has to (CR 509.1c).</summary>
    public bool MustBeBlocked { get; init; }

    /// <summary>
    /// Whether this permanent's activated abilities cannot be activated (CR 602.5c).
    /// </summary>
    /// <remarks>
    /// Every one of them, mana abilities included - the rule draws no distinction, and a
    /// pacifism-style effect that left a land tapping for mana would be a different card.
    /// </remarks>
    public bool AbilitiesCantBeActivated { get; init; }

    /// <summary>Whether this creature has to block something if it can (CR 509.1a).</summary>
    /// <remarks>
    /// The requirement read from the blocker's side rather than the attacker's, which the two
    /// flags above are. A card that compels a particular creature to block is making a demand of
    /// that creature, and nothing about the attacker says so.
    /// </remarks>
    public bool MustBlock { get; init; }

    /// <summary>Whether this permanent is legendary (CR 205.4a, 701.54c).</summary>
    /// <remarks>
    /// Computed rather than read off the card, because an effect can make a permanent legendary
    /// - the Ring does it to its bearer - and the legend rule has to see that. Every other
    /// characteristic here learned the same lesson; this one had simply never been asked.
    /// </remarks>
    public bool IsLegendary { get; init; }

    /// <summary>Whether bigger creatures cannot block this one (CR 701.54c).</summary>
    /// <remarks>
    /// The Ring's first ability, and a restriction of its own rather than an evasion keyword:
    /// what may block depends on the blocker's power against this one's, which no flag can say.
    /// </remarks>
    public bool CantBeBlockedByGreaterPower { get; init; }

    /// <summary>
    /// Whether this creature's controller may assign its combat damage as though it were
    /// unblocked (CR 510.1a).
    /// </summary>
    /// <remarks>
    /// A permission and not an instruction: the printing says "you <em>may</em> have", and taking
    /// it automatically would be wrong in both directions - it is better than blocking normally
    /// against a chump blocker and worse against a creature the attacker wanted dead.
    /// </remarks>
    public bool MayAssignAsThoughUnblocked { get; init; }

    /// <summary>The player this creature must attack if it can (CR 702.141a).</summary>
    /// <remarks>
    /// Goad's mirror: that one says "anybody but this player" and this says "this player and
    /// nobody else". An encore token is made to go at one opponent, and a requirement that only
    /// said "attack somebody" would let it go anywhere.
    /// </remarks>
    public Guid? MustAttackPlayer { get; init; }

    /// <summary>The players who have goaded this creature (CR 701.15b).</summary>
    /// <remarks>
    /// A set rather than one player, because CR 701.15c says a creature can be goaded by several
    /// and each is a requirement of its own: it must attack somebody who is none of them, if it
    /// can. Goaded is neither an ability nor a copiable value (CR 701.15b), which is why it is a
    /// designation computed onto the characteristics rather than a keyword.
    /// </remarks>
    public ImmutableHashSet<Guid> GoadedBy { get; init; } = [];

    /// <summary>Which attacker this creature may not block (CR 509.1b).</summary>
    /// <remarks>
    /// The mirror of <see cref="MustBlockAttacker"/> and a restriction rather than a
    /// requirement, so it is answered where the other evasion abilities are - a restriction
    /// refuses the block outright, and a requirement only complains once the whole set is in.
    /// </remarks>
    public ObjectId? CantBlockAttacker { get; init; }

    /// <summary>Which attacker this creature has to block if it can (CR 509.1a).</summary>
    /// <remarks>
    /// Provoke and its relatives name both halves - this creature, that attacker - and neither
    /// <see cref="MustBlock"/> nor <see cref="MustBeBlocked"/> can say it: the first would be
    /// satisfied by blocking anything and the second by anyone blocking. Held as the attacker's
    /// id because a requirement outlives the declaration it was made during.
    /// </remarks>
    public ObjectId? MustBlockAttacker { get; init; }

    /// <summary>
    /// Whether this is protected from everything the other object is (CR 702.16b).
    /// </summary>
    /// <remarks>
    /// Protection cares about the source's colour, so a multicoloured source is stopped by
    /// protection from any one of its colours.
    /// </remarks>
    public bool IsProtectedFrom(ComputedCharacteristics source)
    {
        ArgumentNullException.ThrowIfNull(source);

        foreach (var (colour, flag) in ProtectionFlags)
        {
            if (Has(flag) && source.Colors.Contains(colour))
                return true;
        }

        // A card type is a quality like a colour, and protection names either (CR 702.16b).
        return Has(KeywordAbility.ProtectionFromArtifacts)
            && source.CardTypes.HasFlag(CardType.Artifact);
    }

    private static readonly (ManaColor Colour, KeywordAbility Flag)[] ProtectionFlags =
    [
        (ManaColor.White, KeywordAbility.ProtectionFromWhite),
        (ManaColor.Blue, KeywordAbility.ProtectionFromBlue),
        (ManaColor.Black, KeywordAbility.ProtectionFromBlack),
        (ManaColor.Red, KeywordAbility.ProtectionFromRed),
        (ManaColor.Green, KeywordAbility.ProtectionFromGreen),
    ];

    /// <summary>Whether it has a creature subtype, honouring changeling (CR 702.73a).</summary>
    public bool HasSubtype(string subtype) =>
        IsEveryCreatureType
        || Subtypes.Contains(subtype, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// What an object's characteristics currently are, as opposed to what was printed on it.
/// </summary>
/// <remarks>
/// Nothing stores a permanent's power. It is computed here, every time, by starting from the
/// printed values and applying every applicable continuous effect in layer order (CR 613.1).
/// <para>
/// This is the direct fix for the bug class that ended the previous engine. There,
/// <c>IStaticAbility.Apply(state) =&gt; state</c> wrote a lord's bonus into each creature as a
/// mutation, so when the lord left the battlefield nothing took the bonus back off. Here a lord's
/// effect exists only while the lord is on the battlefield to produce it, and it stops existing
/// the instant it is not — because the effect is never recorded anywhere in the first place.
/// </para>
/// <para>
/// Recomputed on demand rather than cached. Caching would need invalidating on every change to
/// the battlefield, every counter, and every timestamp, and a stale cache here is exactly the
/// failure being avoided. If it becomes a measured problem, memoise on a state version — do not
/// go back to writing values into permanents.
/// </para>
/// </remarks>
/// <summary>
/// Whether one creature may block another (CR 509.1b).
/// </summary>
/// <remarks>
/// Named rather than left as a <c>Func</c> because two of its arguments are the same type and
/// the order is the whole meaning: the attacker carries the restriction, the blocker is being
/// judged by it. A pair of anonymous game objects says nothing about which is which.
/// <para>
/// It is handed the objects rather than their characteristics so that a restriction can reuse the
/// target grammar's own filters — those want an object and a state, and writing a second filter
/// that reads computed values instead would be a copy of a vocabulary that already exists.
/// </para>
/// </remarks>
public delegate bool BlockRestriction(
    GameState state,
    Abilities.IAbilitySource abilities,
    GameObject attacker,
    GameObject blocker);

public static class Characteristics
{
    /// <summary>Everything about an object, after the layers (CR 613).</summary>
    public static ComputedCharacteristics Of(
        GameState state, IAbilitySource abilities, GameObject obj)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);
        ArgumentNullException.ThrowIfNull(obj);

        // CR 707.2: a face-down permanent is a 2/2 colourless creature with no name, no card
        // types other than creature, no subtypes and no abilities. It is not the card with an
        // effect on it — those characteristics are simply what the object has, which is why this
        // starts somewhere else entirely rather than applying a layer.
        if (obj.Permanent is { IsFaceDown: true })
            return FaceDown(state, abilities, obj);

        var builder = new CharacteristicsBuilder(obj);

        // A keyword the card's own text grants is part of what it is, before any effect applies
        // — the same standing as one printed as a keyword (CR 702.1).
        builder.Keywords |= abilities.GrantedKeywords(obj.Card);

        // CR 702.73a: changeling is a characteristic-defining ability — the card is every
        // creature type in every zone, at all times. It is applied with the printed values
        // rather than as a layer-4 effect for that reason (CR 613.2).
        if (builder.Keywords.HasFlag(KeywordAbility.Changeling))
            builder.IsEveryCreatureType = true;

        // CR 718.2: a prototyped spell uses the inset frame's size instead of the printed one.
        // Applied to the starting values rather than as a layer-7 effect, because these *are*
        // the object's characteristics rather than something changing them - the same standing
        // the printed numbers have, which is why a lord's bonus still adds on top.
        if (obj.WasPrototyped && abilities.SpellOf(obj.Card) is
            { PrototypePower: { } smallPower, PrototypeToughness: { } smallToughness })
        {
            builder.Power = smallPower;
            builder.Toughness = smallToughness;
        }

        return ApplyLayers(state, abilities, obj, builder);
    }

    /// <summary>
    /// Runs CR 613's layers over a builder that is already primed with its starting values.
    /// </summary>
    /// <remarks>
    /// Shared with the face-down path, because what changes there is only where the starting
    /// values come from. Everything else on the board still applies: a lord pumps a face-down
    /// 2/2 exactly as it pumps any other creature, and a face-down permanent that something has
    /// turned into an artifact is an artifact.
    /// </remarks>
    private static ComputedCharacteristics ApplyLayers(
        GameState state, IAbilitySource abilities, GameObject obj, CharacteristicsBuilder builder)
    {
        // CR 613.1: start with the printed values, then apply the effects layer by layer. Within
        // a layer the order is by timestamp (CR 613.7) unless one effect depends on another, in
        // which case dependency wins (CR 613.8).
        foreach (var layer in Candidates(state, abilities, obj)
            .GroupBy(c => c.Effect.Layer)
            .OrderBy(g => (int)g.Key))
        {
            foreach (var candidate in InDependencyOrder(state, [.. layer], builder))
            {
                // Applicability is asked again here rather than reused: an effect earlier in the
                // same layer may have just brought this one into range.
                if (candidate.Effect.Applies(state, candidate.Source, builder))
                    candidate.Effect.Apply(state, candidate.Source, builder);
            }
        }

        // CR 701.54c: the Ring is an emblem rather than a permanent, so its abilities have no
        // source object to hang a continuous effect on. They are applied here, after the layers,
        // because nothing they do interacts with a layer - the legendary supertype is the only
        // characteristic among them, and no card changes a permanent's supertypes.
        ApplyTheRing(state, obj, builder);

        return builder.Build();
    }

    /// <summary>
    /// The abilities the Ring gives its bearer, as far as it has tempted them (CR 701.54c).
    /// </summary>
    /// <remarks>
    /// Each is "as long as the Ring has tempted you N or more times", so they accumulate and
    /// none is ever taken away. The bearer is a designation on the player rather than anything
    /// on the creature (CR 701.54b), which is why this asks the player and not the object.
    /// </remarks>
    private static void ApplyTheRing(GameState state, GameObject obj, CharacteristicsBuilder builder)
    {
        if (!state.Players.TryGetValue(builder.ControllerId, out var bearerOwner)
            || bearerOwner.RingBearer != obj.Id
            || bearerOwner.RingTemptations == 0)
        {
            return;
        }

        // One: legendary, and bigger creatures cannot block it.
        builder.IsLegendary = true;
        builder.CantBeBlockedByGreaterPower = true;

        if (bearerOwner.RingTemptations >= 2)
            builder.GrantedTriggers.Add(RingAbilities.DrawsAndDiscards);

        if (bearerOwner.RingTemptations >= 3)
            builder.GrantedTriggers.Add(RingAbilities.BlockersAreSacrificed);

        if (bearerOwner.RingTemptations >= 4)
            builder.GrantedTriggers.Add(RingAbilities.DrainsOnDamage);
    }

    /// <summary>The characteristics of a face-down permanent (CR 707.2).</summary>
    /// <remarks>
    /// The card underneath contributes nothing at all — not its name, not its types, not its
    /// abilities, not its mana cost. That is what makes a face-down creature safe to reveal
    /// later: the object never had the card's characteristics to lose.
    /// </remarks>
    private static ComputedCharacteristics FaceDown(
        GameState state, IAbilitySource abilities, GameObject obj)
    {
        var builder = new CharacteristicsBuilder(obj)
        {
            Power = 2,
            Toughness = 2,
            CardTypes = CardType.Creature,
            Keywords = KeywordAbility.None,
        };

        builder.Subtypes.Clear();
        builder.Colors.Clear();

        return ApplyLayers(state, abilities, obj, builder);
    }

    /// <summary>Current power (CR 208, 613.4).</summary>
    public static int? PowerOf(GameState state, IAbilitySource abilities, GameObject obj) =>
        Of(state, abilities, obj).Power;

    /// <summary>Current toughness (CR 208, 613.4).</summary>
    public static int? ToughnessOf(GameState state, IAbilitySource abilities, GameObject obj) =>
        Of(state, abilities, obj).Toughness;

    /// <summary>Whether the object is currently a creature (CR 302.1).</summary>
    public static bool IsCreature(GameState state, IAbilitySource abilities, GameObject obj) =>
        Of(state, abilities, obj).IsCreature;

    /// <summary>Whether the object currently has a keyword ability (CR 702).</summary>
    public static bool HasKeyword(
        GameState state, IAbilitySource abilities, GameObject obj, KeywordAbility keyword) =>
        Of(state, abilities, obj).Has(keyword);

    /// <summary>One continuous effect that might apply to the object being computed.</summary>
    private readonly record struct Candidate(
        ContinuousEffectDefinition Effect, GameObject? Source, long Timestamp);

    /// <summary>
    /// Every continuous effect that could apply to this object.
    /// </summary>
    /// <remarks>
    /// Gathered without asking whether each one applies: that question is answered as its layer
    /// is reached, because an effect in an earlier layer can bring a later one into range —
    /// turning a creature white makes an anthem that pumps white creatures start applying to it.
    /// </remarks>
    private static IEnumerable<Candidate> Candidates(
        GameState state, IAbilitySource abilities, GameObject target)
    {
        // Static abilities of permanents on the battlefield (CR 604.2): their effects exist for
        // exactly as long as the permanent does.
        foreach (var id in state.Battlefield)
        {
            var source = state.GetObject(id);
            foreach (var effect in abilities.StaticsOf(source.Card))
                yield return new Candidate(effect, source, source.Timestamp);
        }

        // Counters modify power and toughness in layer 7c (CR 613.4c, 122.1c). They are not a
        // static ability of anything, so they are added here rather than found on a permanent.
        if (target.Permanent is not null && CounterDelta(target) != 0)
            yield return new Candidate(CounterEffect(CounterDelta(target)), null, target.Timestamp);

        // Effects created by a resolved spell or ability, which outlive their source (CR 613.7b).
        foreach (var floating in state.FloatingEffects)
        {
            if (!floating.AffectedIds.Contains(target.Id))
                continue;

            var definition = abilities.FloatingEffect(floating.DefinitionId);
            if (definition is not null)
                yield return new Candidate(definition, null, floating.Timestamp);
        }
    }

    /// <summary>
    /// Orders one layer's effects, letting dependency override timestamp (CR 613.8).
    /// </summary>
    /// <remarks>
    /// CR 613.8a: an effect depends on another when applying that other would change what the
    /// first applies to, or what it does. That is answered by asking — applying the other to a
    /// throwaway copy and seeing whether the first's answer changes — rather than by a table of
    /// special cases, which would only ever cover the cards somebody thought of.
    /// <para>
    /// CR 613.8b: dependents wait until everything they depend on has been applied, and a
    /// dependency loop falls back to timestamp order. The loop case is why this is written as
    /// "take whatever is ready, and if nothing is, take the earliest" rather than as a topological
    /// sort that can fail.
    /// </para>
    /// </remarks>
    private static List<Candidate> InDependencyOrder(
        GameState state, List<Candidate> layer, CharacteristicsBuilder builder)
    {
        var remaining = layer.OrderBy(c => c.Timestamp).ToList();
        if (remaining.Count < 2)
            return remaining;

        var ordered = new List<Candidate>(remaining.Count);

        while (remaining.Count > 0)
        {
            // The earliest effect that nothing else still to come would change.
            var next = remaining.FirstOrDefault(
                c => !remaining.Any(other => !Equals(other, c) && DependsOn(state, c, other, builder)));

            // A dependency loop: CR 613.8b says ignore the rule and use timestamp order.
            if (next == default)
                next = remaining[0];

            ordered.Add(next);
            remaining.Remove(next);
        }

        return ordered;
    }

    /// <summary>Whether applying <paramref name="other"/> would change what this one does.</summary>
    private static bool DependsOn(
        GameState state, Candidate effect, Candidate other, CharacteristicsBuilder builder)
    {
        var before = effect.Effect.Applies(state, effect.Source, builder);

        var probe = builder.Copy();
        if (!other.Effect.Applies(state, other.Source, probe))
            return false;

        other.Effect.Apply(state, other.Source, probe);
        return effect.Effect.Applies(state, effect.Source, probe) != before;
    }

    /// <summary>The +1/+1 and -1/-1 counters on a permanent, netted (CR 122.1c).</summary>
    private static int CounterDelta(GameObject obj)
    {
        if (obj.Permanent is null)
            return 0;

        return obj.Permanent.Counters.GetValueOrDefault(CounterKinds.PlusOnePlusOne)
            - obj.Permanent.Counters.GetValueOrDefault(CounterKinds.MinusOneMinusOne);
    }

    private static ContinuousEffectDefinition CounterEffect(int delta) => new()
    {
        Id = "counters",
        Layer = EffectLayer.PowerToughnessModify,
        Applies = (_, _, _) => true,
        Apply = (_, _, builder) => builder.Modify(delta, delta),
    };
}

/// <summary>The counter kinds the rules name directly (CR 122.1).</summary>
public static class CounterKinds
{
    public const string PlusOnePlusOne = "+1/+1";
    public const string MinusOneMinusOne = "-1/-1";
    public const string Loyalty = "loyalty";

    /// <summary>
    /// Time counters, as vanishing and fading put them on a permanent (CR 702.63a).
    /// </summary>
    /// <remarks>
    /// A suspended card keeps its own count in <c>GameObject.TimeCounters</c> instead, because a
    /// card in exile is not a permanent and has no counters to keep.
    /// </remarks>
    public const string Time = "time";

    /// <summary>Lore counters, which are how a Saga tracks its progress (CR 714.3).</summary>
    public const string Lore = "lore";

    /// <summary>Charge counters, which are what a station card is charged with (CR 702.184a).</summary>
    public const string Charge = "charge";
}
