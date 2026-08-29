using System.Collections.Immutable;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;

namespace MtgEngine.Rules.State;

/// <summary>An object's characteristics after every continuous effect has been applied.</summary>
public sealed record ComputedCharacteristics
{
    /// <summary>
    /// The card these characteristics were computed from, after layer 1 (CR 613.2c).
    /// </summary>
    /// <remarks>
    /// The object's own printed card nearly always, and something else in the two cases the
    /// rules say an object's copiable values are not its card's: a permanent under a copy effect
    /// has the copied card's (CR 707.2), and a face-down one has the characteristics the rules
    /// give it rather than any card's (CR 708.2a).
    /// <para>
    /// It is here because a permanent's <em>abilities</em> are not characteristics and cannot be
    /// made into them. Activated abilities, triggered abilities, replacement effects and static
    /// abilities are all looked up from <see cref="Abilities.IAbilitySource"/> <em>by card</em>,
    /// so every reader that asks a card what a permanent can do has to ask this instead of
    /// asking <see cref="GameObject.Card"/> — the same correction every reader of a stored
    /// controller had to make when control turned out to be layer 2.
    /// </para>
    /// </remarks>
    public required CardDefinition Card { get; init; }

    /// <summary>The name the object has right now (CR 707.2).</summary>
    /// <remarks>
    /// Computed rather than read off the printed card because a name is a copiable value: a
    /// permanent that has become a copy answers to the copied card's name, which is what the
    /// legend rule (CR 704.5j) and every "another permanent with the same name" check are asking
    /// about.
    /// </remarks>
    public string Name => Card.Name;

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

    /// <summary>
    /// Whether this creature may attack as though it did not have defender (CR 609.4, 702.3b).
    /// </summary>
    /// <remarks>
    /// A permission rather than the loss of a keyword, and the two are genuinely different: the
    /// creature still has defender, so anything reading the keyword still sees it and a card that
    /// pumps "creatures with defender" still pumps this one. CR 609.4 says an "as though" effect
    /// applies to the stated effect only, so this is asked in exactly one place — the defender
    /// arm of <see cref="Engine.CombatRules.CannotAttack"/> — and nowhere else.
    /// <para>
    /// It is a characteristic and not a fact about a declaration because most of the cards that
    /// grant it are conditional on the board: "as long as you control a creature with power 4 or
    /// greater", "as long as this creature has a +1/+1 counter on it". Those are static abilities
    /// whose answer changes between one attack and the next, which is what the layers are for.
    /// </para>
    /// </remarks>
    public bool MayAttackAsThoughNoDefender { get; init; }

    /// <summary>
    /// Whether an effect has taken away every ability this object has (CR 613.1f).
    /// </summary>
    /// <remarks>
    /// The keywords and the granted abilities are already gone from this record — layer 6 emptied
    /// them — but the abilities a card is <em>looked up</em> by are not characteristics at all:
    /// activated abilities, triggered abilities and replacement effects all come from
    /// <see cref="IAbilitySource"/> keyed by the card, and no computation here can empty that.
    /// So each of those readers has to ask this, the same way they already ask about a face-down
    /// permanent (CR 707.2), which is the identical shape of problem.
    /// <para>
    /// The one reader that does not have to is this file's own: a permanent that has lost its
    /// abilities stops contributing continuous effects from layer 6 onwards, and that is done
    /// where the effects are gathered rather than by anyone asking.
    /// </para>
    /// </remarks>
    public bool HasLostAllAbilities { get; init; }

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
        // The computation's own ability source rides on the builder, so a filter inside an
        // effect can ask a bounded question about another permanent — the source's layer-2
        // controller through ControllerOf below — without the Applies delegate growing an
        // argument that a hundred construction sites would have to carry and ignore.
        builder.Abilities = abilities;

        // CR 613.2c: once layer 1 is over, the object's characteristics *are* its copiable
        // values — so this is the moment the copied card's own text is read, and the moment the
        // cards under a mutated permanent contribute their abilities (CR 730.2a makes the merge a
        // copiable effect too). Both have to happen before any layer that could add or remove an
        // ability, and exactly once.
        var copiableRead = false;

        void ReadCopiableValues()
        {
            if (copiableRead)
                return;

            copiableRead = true;
            ReadCopiedCard(abilities, obj, builder);
            ReadMergedComponents(abilities, obj, builder);
        }

        // CR 613.1: start with the printed values, then apply the effects layer by layer. Within
        // a layer the order is by timestamp (CR 613.7) unless one effect depends on another, in
        // which case dependency wins (CR 613.8).
        foreach (var layer in Candidates(state, abilities, obj)
            .GroupBy(c => c.Effect.Layer)
            .OrderBy(g => (int)g.Key))
        {
            // Layer 1 is the lowest, so anything else is past it. Hanging this on the *end* of
            // the copy layer alone was wrong in a way nothing noticed for as long as the only
            // caller was the copy read: the loop iterates the layers that have effects in them,
            // so on a board carrying no copy effect there is no layer 1 group and the hook never
            // ran at all. A copy read there is a no-op, which is why it never showed - and a
            // mutated permanent on a quiet board silently had none of its components' abilities.
            if (layer.Key != EffectLayer.Copy)
                ReadCopiableValues();

            foreach (var candidate in InDependencyOrder(state, [.. layer], builder))
            {
                // Applicability is asked again here rather than reused: an effect earlier in the
                // same layer may have just brought this one into range.
                ApplyCandidate(state, candidate, builder);
            }

            if (layer.Key == EffectLayer.Copy)
                ReadCopiableValues();
        }

        // A board with no continuous effects on it at all never entered the loop.
        ReadCopiableValues();

        // CR 701.54c: the Ring is an emblem rather than a permanent, so its abilities have no
        // source object to hang a continuous effect on. They are applied here, after the layers,
        // because nothing they do interacts with a layer - the legendary supertype is the only
        // characteristic among them, and no card changes a permanent's supertypes.
        ApplyTheRing(state, obj, builder);

        return builder.Build();
    }

    /// <summary>
    /// Reads a copied card's text once layer 1 has settled which card that is (CR 707.2a).
    /// </summary>
    /// <remarks>
    /// A copy's abilities are copiable values — CR 707.2a says they are derived from the copied
    /// card's rules text — but nothing in this file holds a permanent's abilities: they are
    /// looked up from an <see cref="IAbilitySource"/> by card, keyed on the card the object
    /// <em>has</em>. So the copied card's activated and triggered abilities are put where a
    /// permanent's non-printed abilities already live, which is the only place a permanent can
    /// hold an ability its own card does not have.
    /// <para>
    /// Done here, at the end of layer 1, rather than in layer 6: an effect that removes every
    /// ability (CR 613.1f) must take a copy's abilities with it whenever it applies, and layer 6
    /// clears these because they were already in the list when it ran. Added in layer 6 instead,
    /// a copy made after a Humility resolved would keep them — timestamp order within the layer
    /// would let the later grant survive, and copiable values do not work that way.
    /// </para>
    /// <para>
    /// The copied card's <em>static</em> abilities are not read here; they are gathered from the
    /// battlefield with every other permanent's, in <see cref="Candidates"/>, because a static
    /// ability is an effect on other objects rather than a characteristic of this one.
    /// </para>
    /// <para>
    /// <b>The copied card's own abilities are not seeded here.</b> They used to be, and that gave
    /// the permanent the copied card's abilities <em>as well as</em> its own — which CR 707.2a
    /// forbids: a copy has the copied card's abilities and not both. The two readers that ask a
    /// permanent what abilities it has, <c>Game.ActivatedAbilitiesOf</c> and
    /// <c>Game.TriggersWatching</c>, now look the printed ones up from
    /// <see cref="ComputedCharacteristics.Card"/> instead of from <see cref="GameObject.Card"/>,
    /// which is the same question asked of the right card. Seeding them here as well would offer
    /// every copied ability twice.
    /// </para>
    /// </remarks>
    private static void ReadCopiedCard(
        IAbilitySource abilities, GameObject obj, CharacteristicsBuilder builder)
    {
        // CR 613.2b: layer 1b is applied after the copy and leaves a face-down permanent with no
        // abilities whatever the copy said (CR 707.3's own worked example).
        if (obj.Permanent is { IsFaceDown: true })
            return;

        var copied = builder.Card;
        if (string.Equals(copied.OracleId, obj.Card.OracleId, StringComparison.Ordinal))
            return;

        // A keyword the copied card's text grants stands exactly as a printed one does
        // (CR 702.1), the same way the object's own text is read before the layers begin.
        builder.Keywords |= abilities.GrantedKeywords(copied);

        // CR 702.73a: changeling is characteristic-defining, so it belongs to whichever card the
        // characteristics are now being read from.
        if (builder.Keywords.HasFlag(KeywordAbility.Changeling))
            builder.IsEveryCreatureType = true;
    }

    /// <summary>
    /// Reads the abilities of every card under the top one (CR 702.140e).
    /// </summary>
    /// <remarks>
    /// "A mutated permanent has all abilities of each card and token that represents it. Its
    /// other characteristics are derived from the topmost card or token." The second sentence is
    /// free: the topmost card <em>is</em> <see cref="GameObject.Card"/>, so the whole engine
    /// already reads the characteristics from it. This is the first sentence, and it has exactly
    /// one place to go — a permanent's abilities are looked up from an
    /// <see cref="IAbilitySource"/> by card, and the only card a lookup can be keyed on is the
    /// one on top. So the components' abilities go where a copy's do and where an Aura's grant
    /// does: <see cref="ComputedCharacteristics.GrantedActivated"/> and its triggered twin, which
    /// <c>Game.ActivatedAbilitiesOf</c> and <c>Game.TriggersWatching</c> already union in.
    /// <para>
    /// Beside the copy read and in the same layer, because CR 730.2a says the merge is a copiable
    /// effect. Which also settles the interaction: an effect that removes all abilities is layer
    /// 6 and runs afterwards, so it takes the components' abilities with it, exactly as it takes
    /// a copied card's.
    /// </para>
    /// <para>
    /// Skipped outright while a copy effect is on the permanent. CR 730.2a makes the merge part
    /// of the copiable values, so a permanent that has become a copy of something else <em>is</em>
    /// that card and nothing more — which is also why a copy of a mutated permanent copies only
    /// the topmost card. Where the two rules could be read either way, this takes the reading
    /// that gives the permanent fewer abilities rather than more.
    /// </para>
    /// </remarks>
    private static void ReadMergedComponents(
        IAbilitySource abilities, GameObject obj, CharacteristicsBuilder builder)
    {
        if (obj.MergedComponents.IsEmpty)
            return;

        // CR 613.2b, as for the copy above: layer 1b leaves a face-down permanent with no
        // abilities whatever is under it.
        if (obj.Permanent is { IsFaceDown: true })
            return;

        if (!string.Equals(builder.Card.OracleId, obj.Card.OracleId, StringComparison.Ordinal))
            return;

        for (var slot = 0; slot < obj.MergedComponents.Count; slot++)
        {
            var component = obj.MergedComponents[slot];

            // A keyword is an ability (CR 702.1), so a stack whose bottom card has flying flies.
            // Both halves are needed: the printed flags and whatever the component's own text
            // grants, which is where the compiler puts a keyword written out as a sentence.
            builder.Keywords |= component.Keywords | abilities.GrantedKeywords(component);

            if (builder.Keywords.HasFlag(KeywordAbility.Changeling))
                builder.IsEveryCreatureType = true;

            var slotPrefix = MergedAbilityPrefix(slot);

            builder.GrantedActivated.AddRange(
                abilities.ActivatedOf(component).Select(a => a with { Id = slotPrefix + a.Id }));

            builder.GrantedTriggers.AddRange(
                abilities.TriggersOf(component).Select(t => t with { Id = slotPrefix + t.Id }));
        }
    }

    /// <summary>
    /// What a component's ability ids are prefixed with, so one stack cannot hold two of a name.
    /// </summary>
    /// <remarks>
    /// An ability id is only ever unique <em>within its card</em> — the compiler numbers them
    /// "t0", "a0" and so on from zero for every card it reads. That is enough everywhere else,
    /// because a permanent's abilities all come from one card. A mutated permanent's do not, and
    /// two cards in one stack collide on the very first ability each of them has.
    /// <para>
    /// What the collision costs is not a duplicate in a list: an ability goes on the stack as an
    /// id, and it is resolved by looking that id up on the permanent's card — so the card
    /// underneath would have its trigger resolved with the <em>top</em> card's effects, silently
    /// and only ever on the cards this feature exists for.
    /// </para>
    /// <para>
    /// Prefixed by position rather than by card, because position is what the stack is: two
    /// copies of one card mutated onto the same permanent are two components and each gets its
    /// own. Stable across a recomputation because the stack's order is, which is what lets a
    /// deferred question and a pending trigger find the ability again.
    /// </para>
    /// </remarks>
    internal static string MergedAbilityPrefix(int slot) =>
        "m" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":";

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

    /// <summary>
    /// The card a face-down spell is, for the questions asked before it is an object (CR 702.37a).
    /// </summary>
    /// <remarks>
    /// <see cref="FaceDown"/> below answers about a permanent, which exists and can be handed to
    /// the layers. A spell's cost is worked out before anything is on the stack at all, and the
    /// two questions asked there — which cost modifiers apply (CR 601.2f) and whether a
    /// restricted mana may pay (CR 106.6) — are both put to a <see cref="CardDefinition"/>.
    /// <para>
    /// The card underneath is the wrong answer to both, and wrong in the direction that makes
    /// the spell better than printed: CR 702.37a casts it as "a 2/2 face-down creature with no
    /// text, no name, no subtypes, and no mana cost", so "Dragon spells cost {1} less to cast"
    /// is not looking at a Dragon and "spend this mana only to cast Dragon spells" may not pay
    /// for one. What both <em>are</em> looking at is a creature spell, which is what this says
    /// and the only thing it says.
    /// </para>
    /// </remarks>
    public static readonly CardDefinition FaceDownSpell = new()
    {
        OracleId = "face-down",
        CardTypes = CardType.Creature,
        Power = 2,
        Toughness = 2,
    };

    /// <summary>The characteristics of a face-down permanent (CR 707.2).</summary>
    /// <remarks>
    /// The card underneath contributes nothing at all — not its name, not its types, not its
    /// abilities, not its mana cost. That is what makes a face-down creature safe to reveal
    /// later: the object never had the card's characteristics to lose.
    /// </remarks>
    private static ComputedCharacteristics FaceDown(
        GameState state, IAbilitySource abilities, GameObject obj)
    {
        var builder = new CharacteristicsBuilder(obj);

        // CR 708.2a gives the values, and CR 708.2 says those values *are* the object's copiable
        // ones — which is why this is written as becoming a copy of a card with nothing on it
        // rather than as four assignments. The name goes with them, and so does the legendary
        // supertype: a face-down permanent has no name to be a second copy of (CR 704.5j).
        builder.BecomeCopyOf(FaceDownSpell);

        return ApplyLayers(state, abilities, obj, builder);
    }

    /// <summary>
    /// Which card an object's characteristics are read from, without running the layers.
    /// </summary>
    /// <remarks>
    /// The same answer as <c>Of(state, abilities, obj).Card</c> and much cheaper, because layer
    /// 1 is the only layer that can change it (CR 613.2c). It exists because the full
    /// computation cannot be used here: <see cref="Candidates"/> asks this of every permanent on
    /// the battlefield while gathering their static abilities, and asking
    /// <see cref="Of(GameState, IAbilitySource, GameObject)"/> there would recurse without
    /// bottom.
    /// <para>
    /// Only floating copy effects are consulted (CR 613.7b) — a copy created by a resolved spell
    /// or ability, which is every copy effect the engine can produce. A copy effect generated by
    /// a <em>static</em> ability (CR 707.2c) would need this to ask the battlefield what it is
    /// while it is deciding what the battlefield is, and the answer to that is the recursion
    /// above rather than a deeper limit.
    /// </para>
    /// </remarks>
    public static CardDefinition CardOf(
        GameState state, IAbilitySource abilities, GameObject obj)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);
        ArgumentNullException.ThrowIfNull(obj);

        // CR 708.2: a face-down permanent's characteristics come from the rules, not from the
        // card underneath — which is what keeps the card safe to reveal later.
        if (obj.Permanent is { IsFaceDown: true })
            return FaceDownSpell;

        return CopiedAs(state, abilities, obj.Id, obj.Card);
    }

    /// <summary>
    /// The same question, asked about an id that need not name an object yet (CR 400.7).
    /// </summary>
    /// <remarks>
    /// A permanent entering as a copy is copied under the id it is <em>about to</em> have. Its
    /// spell is still on the stack while the arrival is being replaced, and the copy effect names
    /// the permanent it is becoming, not the spell — so a caller working out what is arriving has
    /// an id and a printed card and no object to hand over.
    /// </remarks>
    public static CardDefinition CopiedAs(
        GameState state, IAbilitySource abilities, ObjectId id, CardDefinition printed)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);
        ArgumentNullException.ThrowIfNull(printed);

        if (state.FloatingEffects.IsEmpty)
            return printed;

        CardDefinition? copied = null;
        var latest = long.MinValue;

        foreach (var floating in state.FloatingEffects)
        {
            if (!floating.AffectedIds.Contains(id))
                continue;

            // Through the same guard the layers use, so a definition that copies in the wrong
            // layer is refused here as well rather than being quietly honoured by one of the two
            // readers and thrown out by the other.
            if (abilities.FloatingEffect(floating.DefinitionId) is not { } definition
                || Copies(definition) is not { } card)
            {
                continue;
            }

            // CR 613.7: within a layer, timestamp order — so the last copy effect to have been
            // created is the one whose values survive.
            if (floating.Timestamp >= latest)
            {
                latest = floating.Timestamp;
                copied = card;
            }
        }

        return copied ?? printed;
    }

    /// <summary>
    /// Whether a controller-of question is already being answered, so one cannot recurse.
    /// </summary>
    /// <remarks>
    /// A control effect that asked who controls something while that question was being
    /// answered would recurse without bottom. CR 613.8b resolves a dependency loop by falling
    /// back rather than looping, and so does this: the nested ask is answered with the stored
    /// controller, which is where layer 2 starts from. No control effect in the engine asks one
    /// today — all three producers read raw state — so the bound is a guarantee rather than a
    /// behaviour anything reaches.
    /// </remarks>
    [ThreadStatic]
    private static bool _askingWhoControls;

    /// <summary>
    /// Who controls a permanent, asking only layer 2 (CR 613.1b) — safe from inside the layers.
    /// </summary>
    /// <remarks>
    /// <see cref="Of(GameState, IAbilitySource, GameObject)"/> answers the same question and
    /// cannot be used where this one is needed: a mass static's <c>Applies</c> has to know its
    /// <em>source's</em> controller, and computing the source's full characteristics from inside
    /// another permanent's computation recurses — lord A's filter computes lord B, whose filter
    /// computes lord A (the CR 613.8 hazard). Control is settled in layer 2 and nothing after
    /// layer 2 changes it, so the question is answerable from the control-layer effects alone,
    /// none of whose predicates re-enter the layers.
    /// <para>
    /// This is the fix for the stolen-lord defect: reading <c>source.ControllerId</c> raw is
    /// where control <em>started</em> (CR 613.1b), so a stolen lord kept buffing its old
    /// controller's creatures. Reading it here follows the theft.
    /// </para>
    /// </remarks>
    public static Guid ControllerOf(GameState state, IAbilitySource abilities, GameObject obj)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);
        ArgumentNullException.ThrowIfNull(obj);

        // Control-changing effects apply to permanents; everywhere else the stored controller
        // is the whole answer (CR 108.4).
        if (obj.Zone != Zone.Battlefield)
            return obj.ControllerId;

        if (_askingWhoControls)
            return obj.ControllerId;

        _askingWhoControls = true;
        try
        {
            List<Candidate>? candidates = null;

            // Static control effects — "you control enchanted creature" — gathered the way the
            // full computation gathers them, through the card the permanent *is* (CR 707.2a).
            foreach (var id in state.Battlefield)
            {
                var source = state.GetObject(id);

                foreach (var effect in abilities.StaticsOf(CardOf(state, abilities, source)))
                {
                    if (effect.Layer == EffectLayer.Control)
                        (candidates ??= []).Add(new Candidate(effect, source, source.Timestamp));
                }

                foreach (var component in source.MergedComponents)
                {
                    foreach (var effect in abilities.StaticsOf(component))
                    {
                        if (effect.Layer == EffectLayer.Control)
                            (candidates ??= []).Add(
                                new Candidate(effect, source, source.Timestamp));
                    }
                }
            }

            // Floating control effects — a resolved Act of Treason (CR 613.7b).
            foreach (var floating in state.FloatingEffects)
            {
                if (!floating.AffectedIds.Contains(obj.Id))
                    continue;

                if (abilities.FloatingEffect(floating.DefinitionId) is
                    { Layer: EffectLayer.Control } definition)
                {
                    (candidates ??= []).Add(new Candidate(definition, null, floating.Timestamp));
                }
            }

            // The ordinary board: nothing anywhere moves control, so the stored controller is
            // the computed one and no builder has to be made.
            if (candidates is null)
                return obj.ControllerId;

            var builder = new CharacteristicsBuilder(obj) { Abilities = abilities };

            foreach (var candidate in InDependencyOrder(state, candidates, builder))
                ApplyCandidate(state, candidate, builder);

            return builder.ControllerId;
        }
        finally
        {
            _askingWhoControls = false;
        }
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
    private static List<Candidate> Candidates(
        GameState state, IAbilitySource abilities, GameObject target)
    {
        var found = new List<Candidate>();

        // Which permanents could have an effect of theirs dropped, and whether anything on the
        // board is dropping one. Both are collected while the effects are being gathered anyway,
        // so a board with no ability-removal on it pays for one comparison per effect and the
        // rest of this method never runs. The set is left null until something needs to be in it,
        // because this is the hottest path in the engine and most boards never fill one.
        HashSet<ObjectId>? silenceable = null;
        var removing = false;

        // Static abilities of permanents on the battlefield (CR 604.2): their effects exist for
        // exactly as long as the permanent does.
        foreach (var id in state.Battlefield)
        {
            var source = state.GetObject(id);

            // Asked of the card the permanent *is*, not the one it was printed as. A permanent
            // that has become a copy of a lord has the lord's static ability (CR 707.2a), and a
            // card-keyed lookup on its own printed card reports no such ability - the same trap
            // the activated abilities were in before Game.ActivatedAbilitiesOf existed.
            foreach (var effect in abilities.StaticsOf(CardOf(state, abilities, source)))
            {
                if (effect.Layer >= EffectLayer.Ability)
                    (silenceable ??= []).Add(source.Id);

                removing |= Removes(effect);
                found.Add(new Candidate(effect, source, source.Timestamp));
            }

            // CR 702.140e: a mutated permanent has the abilities of every card representing it,
            // and a static ability is an ability. It is gathered here rather than in
            // ReadMergedComponents with the rest, for the reason the copy read gives: a static
            // ability is an effect on other objects, not a characteristic of this one, so it is
            // found by sweeping the battlefield and never by asking one permanent what it is.
            // Nearly always an empty list, and skipped without a lookup when it is.
            foreach (var component in source.MergedComponents)
            {
                foreach (var effect in abilities.StaticsOf(component))
                {
                    if (effect.Layer >= EffectLayer.Ability)
                        (silenceable ??= []).Add(source.Id);

                    removing |= Removes(effect);
                    found.Add(new Candidate(effect, source, source.Timestamp));
                }
            }
        }

        // Counters modify power and toughness in layer 7c (CR 613.4c, 122.1c). They are not a
        // static ability of anything, so they are added here rather than found on a permanent.
        var counters = CounterModifier(target);
        if (target.Permanent is not null && (counters.Power != 0 || counters.Toughness != 0))
        {
            found.Add(new Candidate(
                CounterEffect(counters.Power, counters.Toughness), null, target.Timestamp));
        }

        // Effects created by a resolved spell or ability, which outlive their source (CR 613.7b).
        // A floating effect is not an ability of any permanent, so nothing ever silences one -
        // a creature pumped and then stripped of its abilities keeps the bonus.
        foreach (var floating in state.FloatingEffects)
        {
            // A removal aimed somewhere else is still this computation's business, because what
            // it silences is that permanent's lord and this object may be standing under it.
            // Resolving every floating effect to find out would be the expensive way to ask, so
            // only the ones aimed at a permanent that has an effect to lose are looked up.
            var couldSilence = !removing
                && silenceable is not null
                && floating.AffectedIds.Any(silenceable.Contains);

            if (!couldSilence && !floating.AffectedIds.Contains(target.Id))
                continue;

            var definition = abilities.FloatingEffect(floating.DefinitionId);
            if (definition is null)
                continue;

            removing |= Removes(definition);

            if (floating.AffectedIds.Contains(target.Id))
                found.Add(new Candidate(definition, null, floating.Timestamp));
        }

        return removing && silenceable is not null
            ? WithoutSilencedSources(state, abilities, found, silenceable)
            : found;
    }

    /// <summary>Whether an effect takes every ability away, refusing one in the wrong layer.</summary>
    /// <remarks>
    /// Layer 6 is where abilities are removed and there is no other (CR 613.1f). A definition
    /// saying otherwise is a mistake in whatever built it, and one that would be invisible: it
    /// would take the keywords off in the wrong place and leave the object's own static
    /// abilities applying, which is half a removal and looks like a working card.
    /// </remarks>
    private static bool Removes(ContinuousEffectDefinition effect)
    {
        if (!effect.RemovesAllAbilities)
            return false;

        if (effect.Layer != EffectLayer.Ability)
        {
            throw new InvalidOperationException(
                $"'{effect.Id}' removes all abilities in layer {effect.Layer}; "
                + "ability removal is layer 6 (CR 613.1f).");
        }

        return true;
    }

    /// <summary>The card a copy effect grants, refusing one outside layer 1 (CR 613.2a).</summary>
    /// <remarks>
    /// Layer 1 is where copiable values are modified and there is no other (CR 613.1a). A
    /// definition saying otherwise is a mistake in whatever built it, and an invisible one: the
    /// copy would land after some of the layers that are meant to apply on top of it, so a
    /// creature that became a copy would lose the pump it was given a moment earlier and the
    /// card would still look as though it worked.
    /// </remarks>
    private static CardDefinition? Copies(ContinuousEffectDefinition effect)
    {
        if (effect.Copies is not { } card)
            return null;

        if (effect.Layer != EffectLayer.Copy)
        {
            throw new InvalidOperationException(
                $"'{effect.Id}' copies a card in layer {effect.Layer}; "
                + "copy effects are layer 1 (CR 613.2a).");
        }

        return card;
    }

    /// <summary>
    /// Whether a nested "has that permanent lost its abilities?" is already being answered.
    /// </summary>
    /// <remarks>
    /// The question is asked of another permanent, and answering it computes that permanent's
    /// characteristics, which asks it again of everything on the battlefield. One level deep is
    /// where this stops: a permanent whose abilities were removed by a permanent that had
    /// <em>its</em> abilities removed is answered as though the second removal had not happened.
    /// <para>
    /// The alternative is not a deeper limit, it is a cycle — two permanents can each remove the
    /// other's abilities, and the rules have no answer for that either (CR 613.8b takes the same
    /// way out for dependency loops). Bounded here rather than guarded per object because the
    /// bound is what makes the cost of a removal on the board O(lords) instead of unbounded.
    /// </para>
    /// </remarks>
    [ThreadStatic]
    private static bool _askingWhoLostAbilities;

    /// <summary>
    /// Drops the effects of permanents that have lost every ability (CR 613.1f, 613.6).
    /// </summary>
    /// <remarks>
    /// Only from layer 6 onwards, and CR 613.6 is why: an effect that has already started to
    /// apply keeps applying "even if the ability generating the effect is removed during this
    /// process". Layers 1 to 5 have run by the time abilities come off, so a permanent's own
    /// type-changing or colour-changing static still counts; its lord's bonus in layer 7c does
    /// not, because that layer had not been reached yet.
    /// </remarks>
    private static List<Candidate> WithoutSilencedSources(
        GameState state,
        IAbilitySource abilities,
        List<Candidate> found,
        HashSet<ObjectId> silenceable)
    {
        if (_askingWhoLostAbilities)
            return found;

        var silenced = new HashSet<ObjectId>();

        _askingWhoLostAbilities = true;
        try
        {
            foreach (var id in silenceable)
            {
                if (state.TryGetObject(id, out var source)
                    && Of(state, abilities, source).HasLostAllAbilities)
                {
                    silenced.Add(id);
                }
            }
        }
        finally
        {
            _askingWhoLostAbilities = false;
        }

        if (silenced.Count == 0)
            return found;

        return
        [
            .. found.Where(c =>
                c.Effect.Layer < EffectLayer.Ability
                || c.Source is null
                || !silenced.Contains(c.Source.Id)),
        ];
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
        if (!ApplyCandidate(state, other, probe))
            return false;

        return effect.Effect.Applies(state, effect.Source, probe) != before;
    }

    /// <summary>
    /// Applies one effect to the characteristics as they stand, if it applies at all.
    /// </summary>
    /// <remarks>
    /// Shared with the dependency probe (CR 613.8a), which has to apply an effect to a throwaway
    /// copy and see what changed. Written once so the two cannot come to disagree about what
    /// applying an effect means — an ability-removing effect that the probe applied as a no-op
    /// would make every effect look independent of it, which is exactly backwards: removing
    /// abilities is the commonest thing there is for another effect to depend on.
    /// </remarks>
    private static bool ApplyCandidate(
        GameState state, Candidate candidate, CharacteristicsBuilder builder)
    {
        if (!candidate.Effect.Applies(state, candidate.Source, builder))
            return false;

        // CR 613.2a, and declared on the definition rather than done inside its Apply — see
        // ContinuousEffectDefinition.Copies. Refused outside layer 1 for the reason an ability
        // removal is refused outside layer 6.
        //
        // Skipped for a face-down permanent, which is not an exception to the rule but the rest
        // of it: layer 1b runs after the copy and replaces every value it set (CR 613.2b), so
        // the object shows the rules' 2/2 whatever it is a copy of — CR 707.3 works the case
        // through, and the only thing lost by not applying the copy at all is what the permanent
        // would become if it were later turned face up.
        if (Copies(candidate.Effect) is { } copied
            && builder.Subject.Permanent is not { IsFaceDown: true })
        {
            builder.BecomeCopyOf(copied);
        }

        // CR 613.1f, and declared on the definition rather than done inside its Apply — see
        // ContinuousEffectDefinition.RemovesAllAbilities for why it cannot be.
        if (candidate.Effect.RemovesAllAbilities)
            builder.LoseAllAbilities();

        candidate.Effect.Apply(state, candidate.Source, builder);
        return true;
    }

    /// <summary>
    /// What a permanent's counters do to its power and toughness (CR 122.1c).
    /// </summary>
    /// <remarks>
    /// Read out of each counter's own <em>name</em> rather than from a list of the two the rest
    /// of the rules single out. CR 122.1c says a counter whose name is a power/toughness modifier
    /// modifies power by the first number and toughness by the second, for any pair — and ten
    /// printed kinds are neither +1/+1 nor -1/-1: Clockwork Beast arrives with seven +1/+0, Ebon
    /// Praetor takes a -2/-2, Wall of Roots pays with a -0/-1. Pinned to the two names, every one
    /// of those was a counter that went onto the permanent, appeared in the log, and did nothing.
    /// <para>
    /// Summed rather than netted, because the two halves are no longer the same number: a
    /// creature holding a +1/+0 and a -0/-1 is one bigger and one smaller, and a single delta has
    /// nowhere to put that. The two named constants still travel this path like any other name —
    /// they are singled out elsewhere (CR 704.5q annihilates only those two) and not here.
    /// </para>
    /// </remarks>
    private static (int Power, int Toughness) CounterModifier(GameObject obj)
    {
        if (obj.Permanent is null)
            return (0, 0);

        var power = 0;
        var toughness = 0;

        foreach (var (kind, many) in obj.Permanent.Counters)
        {
            if (CounterKinds.PowerToughnessOf(kind) is not { } modifier)
                continue;

            power += modifier.Power * many;
            toughness += modifier.Toughness * many;
        }

        return (power, toughness);
    }

    private static ContinuousEffectDefinition CounterEffect(int power, int toughness) => new()
    {
        Id = "counters",
        Layer = EffectLayer.PowerToughnessModify,
        Applies = (_, _, _) => true,
        Apply = (_, _, builder) => builder.Modify(power, toughness),
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

    /// <summary>
    /// Level counters, which are how a leveler tracks how far it has been levelled (CR 711.2a).
    /// </summary>
    /// <remarks>
    /// A real counter, unlike the class level beside it. CR 711.4 says level counters and class
    /// levels are different things that do not interact, and this is the half that is a counter:
    /// proliferate adds one, "remove a counter" takes one off, and a card that counts counters on
    /// a permanent counts these. Putting a leveler's progress in <c>PermanentState.Level</c>
    /// would have hidden it from all three.
    /// </remarks>
    public const string Level = "level";

    /// <summary>
    /// The power and toughness a counter's name modifies by, or null if it names none
    /// (CR 122.1c).
    /// </summary>
    /// <remarks>
    /// The rule is about the shape of the name, not about a list of names: "+1/+1", "-0/-1" and
    /// "+2/+0" are all power/toughness counters and "charge" is not, and nothing has to be told
    /// which is which. Asking the name is what lets the compiler read a counter the engine has
    /// never seen and the layers apply it without either of them keeping a list.
    /// <para>
    /// Both halves must carry a sign, which is what keeps this from reading a name that merely
    /// contains a slash. There is no such counter printed today, and a reader that would accept
    /// one is a reader that will accept one.
    /// </para>
    /// </remarks>
    public static (int Power, int Toughness)? PowerToughnessOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var slash = name.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
            return null;

        return SignedNumber(name[..slash]) is { } power
            && SignedNumber(name[(slash + 1)..]) is { } toughness
            ? (power, toughness)
            : null;
    }

    /// <summary>One half of a counter's name — a sign and then digits, and nothing else.</summary>
    private static int? SignedNumber(string half)
    {
        if (half.Length < 2 || half[0] is not ('+' or '-'))
            return null;

        if (!int.TryParse(
            half.AsSpan(1),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var digits))
        {
            return null;
        }

        return half[0] == '-' ? -digits : digits;
    }
}
