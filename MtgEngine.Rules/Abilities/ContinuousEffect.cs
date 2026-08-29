using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Abilities;

/// <summary>
/// The layers continuous effects are applied in (CR 613.1, 613.4).
/// </summary>
/// <remarks>
/// Numbered with gaps so a sublayer can be inserted without renumbering, and ordered so that
/// sorting by the enum value is the rules' order. Layer 7's sublayers are the ones that come up
/// constantly: setting power and toughness (7b) happens before modifying it (7c), which is why
/// a creature that "becomes 0/1" and has a +1/+1 counter is 1/2 and not 0/1.
/// </remarks>
public enum EffectLayer
{
    /// <summary>
    /// Copiable values (CR 613.1a), which is where a copy effect applies (CR 613.2a).
    /// </summary>
    /// <remarks>
    /// The first layer, and the only one that changes <em>which card</em> the rest of the
    /// computation reads from: CR 613.2c says the object's characteristics are its copiable
    /// values once layer 1 is done, and CR 707.2a derives the copy's abilities from the copied
    /// card's rules text. Everything after this point is applied on top of the copy.
    /// </remarks>
    Copy = 100,

    /// <summary>Control-changing effects (CR 613.1b).</summary>
    Control = 200,

    /// <summary>Text-changing effects (CR 613.1c).</summary>
    Text = 300,

    /// <summary>Type-changing effects (CR 613.1d).</summary>
    Type = 400,

    /// <summary>Color-changing effects (CR 613.1e).</summary>
    Color = 500,

    /// <summary>Ability-adding and ability-removing effects (CR 613.1f).</summary>
    Ability = 600,

    /// <summary>Characteristic-defining power/toughness (CR 613.4a).</summary>
    PowerToughnessCda = 710,

    /// <summary>Effects that set power and/or toughness to a value (CR 613.4b).</summary>
    PowerToughnessSet = 720,

    /// <summary>Effects and counters that modify power and/or toughness (CR 613.4c).</summary>
    PowerToughnessModify = 730,

    /// <summary>Effects that switch power and toughness (CR 613.4d).</summary>
    PowerToughnessSwitch = 740,
}

/// <summary>
/// The characteristics of an object while they are being worked out, layer by layer.
/// </summary>
/// <remarks>
/// Mutable on purpose, and only ever during one computation. CR 613.1 describes exactly this:
/// start from the printed values and apply each applicable effect in order. An effect in layer 5
/// changing a creature's colour can bring a layer 7c effect into range that was not applying a
/// moment ago, so the layers have to run over one accumulating object rather than being combined
/// at the end.
/// </remarks>
public sealed class CharacteristicsBuilder
{
    internal CharacteristicsBuilder(GameObject obj)
    {
        Subject = obj;
        Card = obj.Card;
        Power = obj.Card.Power;
        Toughness = obj.Card.Toughness;
        CardTypes = obj.Card.CardTypes;
        Keywords = obj.Card.Keywords;
        ControllerId = obj.ControllerId;
        IsLegendary = obj.Card.Supertypes.Contains("Legendary", StringComparer.OrdinalIgnoreCase);
        Subtypes = [.. obj.Card.Subtypes];
        Colors = [.. obj.Card.Colors];
    }

    /// <summary>The permanent being computed, with its printed card and its counters.</summary>
    public GameObject Subject { get; }

    /// <summary>
    /// The card the copiable values come from — the object's own, until a copy effect (CR 707.2).
    /// </summary>
    /// <remarks>
    /// Held here because a copy is not only a set of numbers. A permanent's abilities are looked
    /// up from a <see cref="CardDefinition"/> and are not characteristics at all, so the thing
    /// that has to change when one permanent becomes another is <em>which card is asked</em>.
    /// Rewriting the power, toughness, types and colours alone produces a permanent the right
    /// size with none of the copied card's behaviour, which is the failure this field exists to
    /// prevent.
    /// </remarks>
    public CardDefinition Card { get; private set; }

    public int? Power { get; set; }

    public int? Toughness { get; set; }

    public CardType CardTypes { get; set; }

    public KeywordAbility Keywords { get; set; }

    /// <summary>CR 702.73a: whether this is every creature type (changeling).</summary>
    public bool IsEveryCreatureType { get; set; }

    /// <summary>
    /// Whether this permanent skips its controller's untap step (CR 502.3).
    /// </summary>
    /// <remarks>
    /// A restriction rather than an ability, so it is not a keyword and cannot be granted by
    /// flag. It is computed with everything else because the commonest way to get it is from
    /// somewhere other than the card — an Aura that says "enchanted creature doesn't untap" is
    /// one permanent putting the restriction on another, and only the layers know that.
    /// </remarks>
    public bool DoesNotUntap { get; set; }

    /// <summary>Activated abilities added in layer 6 (CR 613.1f).</summary>
    public List<ActivatedAbilityDefinition> GrantedActivated { get; } = [];

    /// <summary>Triggered abilities added in layer 6 (CR 613.1f).</summary>
    /// <remarks>
    /// Kept apart from the activated ones because everything that reads them is different: an
    /// activated ability is offered to its controller and a triggered one is watching every
    /// event in the game, so the two are asked for in different places and would only have been
    /// separated again at every call site.
    /// </remarks>
    public List<TriggeredAbilityDefinition> GrantedTriggers { get; } = [];

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
    public List<State.BlockRestriction> BlockRestrictions { get; } = [];

    /// <summary>
    /// How many creatures beyond the first this one may block (CR 509.1a).
    /// </summary>
    public int ExtraBlocks { get; set; }

    /// <summary>
    /// The fewest creatures that may block this one at once, as menace generalises (CR 509.1b).
    /// </summary>
    /// <remarks>
    /// Menace is this with a minimum of two, and is kept as its own keyword because the printed
    /// word is what most cards carry. The count exists for the cards that name a bigger number -
    /// "can't be blocked except by three or more creatures" - which menace has no way to say.
    /// </remarks>
    public int MinBlockers { get; set; }

    /// <summary>Whether every creature able to block this one has to (CR 509.1c).</summary>
    public bool MustBeBlockedByAll { get; set; }

    /// <summary>
    /// Whether at least one creature able to block this one has to (CR 509.1c).
    /// </summary>
    /// <remarks>
    /// The weaker of the two requirements, and a different rule rather than a smaller lure: a
    /// lure compels *every* creature that can and this compels *one*. Two flags because a
    /// creature can carry both and the check for each is different.
    /// </remarks>
    public bool MustBeBlocked { get; set; }

    /// <summary>Whether this creature has to block something if it can (CR 509.1a).</summary>
    public bool MustBlock { get; set; }

    /// <summary>Whether this permanent's activated abilities can be activated (CR 602.5c).</summary>
    public bool AbilitiesCantBeActivated { get; set; }

    /// <summary>Which attacker this creature has to block if it can (CR 509.1a).</summary>
    public ObjectId? MustBlockAttacker { get; set; }

    /// <summary>Which attacker this creature may not block (CR 509.1b).</summary>
    public ObjectId? CantBlockAttacker { get; set; }

    /// <summary>The players who have goaded this creature (CR 701.15b).</summary>
    public HashSet<Guid> GoadedBy { get; } = [];

    /// <summary>The player this creature must attack if it can (CR 702.141a).</summary>
    public Guid? MustAttackPlayer { get; set; }

    /// <summary>Whether bigger creatures cannot block this one (CR 701.54c).</summary>
    public bool CantBeBlockedByGreaterPower { get; set; }

    /// <summary>CR 510.1a: damage may be assigned as though nothing were blocking.</summary>
    public bool MayAssignAsThoughUnblocked { get; set; }

    /// <summary>CR 609.4, 702.3b: it may attack as though it did not have defender.</summary>
    /// <remarks>
    /// A permission, not the removal of a keyword. The creature still <em>has</em> defender —
    /// anything that asks whether it does gets yes, and a second effect keyed to defender
    /// ("creatures with defender get +0/+2") keeps applying to it. Only the one rule the
    /// permission names is treated as though the keyword were absent, which is exactly what
    /// CR 609.4 says an "as though" effect does.
    /// </remarks>
    public bool MayAttackAsThoughNoDefender { get; set; }

    /// <summary>Whether this permanent is legendary (CR 205.4a).</summary>
    public bool IsLegendary { get; set; }

    /// <summary>Whether an effect has taken every ability away (CR 613.1f).</summary>
    /// <remarks>
    /// Set only by <see cref="LoseAllAbilities"/>, which only <see cref="State.Characteristics"/>
    /// calls, and only for an effect that declared
    /// <see cref="ContinuousEffectDefinition.RemovesAllAbilities"/>. It is on the builder rather
    /// than worked out afterwards because layer 6 runs in timestamp order (CR 613.7): a grant
    /// applied before the loss is wiped by it and one applied after survives, and neither is
    /// visible once the layer is over.
    /// </remarks>
    public bool HasLostAllAbilities { get; private set; }

    /// <summary>
    /// Takes every ability this object has (CR 613.1f).
    /// </summary>
    /// <remarks>
    /// Only the abilities the <em>object</em> has. What another permanent's ability does to it —
    /// an Aura saying "enchanted creature can't block", a lord's bonus — is that permanent's
    /// ability and is untouched, so the combat requirements and restrictions collected here are
    /// deliberately left alone. The object's own static abilities never reach them: they are
    /// dropped before the layer runs, in <see cref="State.Characteristics"/>.
    /// <para>
    /// Two things it does not clear, and both are the layer order rather than an omission.
    /// Changeling is a characteristic-defining ability applied with the printed types, and the
    /// types it set were settled in layer 4 — CR 613.6 says an effect that has started to apply
    /// keeps applying even once the ability generating it is removed, so the creature stays every
    /// creature type. Legendary is a supertype (CR 205.4a) and was never an ability at all.
    /// </para>
    /// </remarks>
    public void LoseAllAbilities()
    {
        HasLostAllAbilities = true;
        Keywords = KeywordAbility.None;
        GrantedActivated.Clear();
        GrantedTriggers.Clear();
    }

    /// <summary>
    /// Takes another card's copiable values, which is layer 1a (CR 613.2a, 707.2).
    /// </summary>
    /// <remarks>
    /// CR 707.2 lists exactly what is copied — name, mana cost, colour indicator, card type,
    /// subtype, supertype, rules text, power and toughness — and exactly what is not: counters,
    /// damage, status, and every other effect already applying to the object. So this replaces
    /// each copiable value rather than adding to it, and touches nothing else. Control is
    /// untouched because control is layer 2 (CR 613.1b) and a copy is not a change of
    /// controller; the counters are untouched because layer 7c applies them on top of whatever
    /// the copy left, which is how a 1/1 with a +1/+1 counter that copies a 3/3 comes out a 4/4.
    /// <para>
    /// It changes <see cref="Card"/> as well, and that is the half that matters. The copied
    /// card's <em>abilities</em> are not characteristics: they are read from its rules text
    /// (CR 707.2a) through an <see cref="IAbilitySource"/> keyed by card, so a copy that only
    /// rewrote the numbers would have none of them.
    /// </para>
    /// </remarks>
    public void BecomeCopyOf(CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);

        Card = card;
        Power = card.Power;
        Toughness = card.Toughness;
        CardTypes = card.CardTypes;
        Keywords = card.Keywords;

        // A supertype is a copiable value (CR 707.2), so a copy of a legendary permanent is
        // legendary and the legend rule sees two of them (CR 704.5j).
        IsLegendary = card.Supertypes.Contains("Legendary", StringComparer.OrdinalIgnoreCase);

        // Changeling is a characteristic-defining ability of the copied card, not of this one
        // (CR 702.73a), so it is cleared here and re-asked of the copied text afterwards.
        IsEveryCreatureType = false;

        Subtypes.Clear();
        Subtypes.AddRange(card.Subtypes);
        Colors.Clear();
        Colors.AddRange(card.Colors);
    }

    public Guid ControllerId { get; set; }

    public List<string> Subtypes { get; }

    public List<ManaColor> Colors { get; }

    /// <summary>Adds to power and toughness, the layer 7c operation (CR 613.4c).</summary>
    public void Modify(int power, int toughness)
    {
        if (Power is not null)
            Power += power;

        if (Toughness is not null)
            Toughness += toughness;
    }

    /// <summary>Sets power and toughness, the layer 7b operation (CR 613.4b).</summary>
    public void Set(int power, int toughness)
    {
        Power = power;
        Toughness = toughness;
    }

    /// <summary>Swaps power and toughness, the layer 7d operation (CR 613.4d).</summary>
    public void Switch() => (Power, Toughness) = (Toughness, Power);

    /// <summary>Whether the object currently has a subtype, after any type-changing effect.</summary>
    public bool HasSubtype(string subtype) =>
        Subtypes.Contains(subtype, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether it is currently a creature (CR 302.1), after layer 4.</summary>
    public bool IsCreature => CardTypes.HasFlag(CardType.Creature);

    /// <summary>Whether it currently has a colour, after layer 5.</summary>
    public bool IsColor(ManaColor color) => Colors.Contains(color);

    /// <summary>
    /// A copy, for testing what an effect would do without committing to it.
    /// </summary>
    /// <remarks>
    /// Dependency (CR 613.8) is decided by asking whether applying one effect would change what
    /// another applies to. Answering that needs a throwaway copy — the real one is mid-flight.
    /// </remarks>
    internal CharacteristicsBuilder Copy()
    {
        var copy = new CharacteristicsBuilder(Subject)
        {
            // The copied card travels with the probe. One that fell back to the object's own
            // card would decide dependency against text the object no longer has, the moment
            // anything on the board is a copy (CR 613.8a).
            Card = Card,
            Power = Power,
            Toughness = Toughness,
            CardTypes = CardTypes,
            Keywords = Keywords,
            IsEveryCreatureType = IsEveryCreatureType,
            DoesNotUntap = DoesNotUntap,
            ControllerId = ControllerId,
            ExtraBlocks = ExtraBlocks,
            MinBlockers = MinBlockers,
            MustBeBlockedByAll = MustBeBlockedByAll,
            MustBeBlocked = MustBeBlocked,
            MustBlock = MustBlock,
            MustBlockAttacker = MustBlockAttacker,
            CantBlockAttacker = CantBlockAttacker,
            AbilitiesCantBeActivated = AbilitiesCantBeActivated,
            MustAttackPlayer = MustAttackPlayer,
            CantBeBlockedByGreaterPower = CantBeBlockedByGreaterPower,
            MayAssignAsThoughUnblocked = MayAssignAsThoughUnblocked,
            MayAttackAsThoughNoDefender = MayAttackAsThoughNoDefender,
            HasLostAllAbilities = HasLostAllAbilities,
            IsLegendary = IsLegendary,
        };

        copy.GoadedBy.UnionWith(GoadedBy);
        copy.GrantedActivated.AddRange(GrantedActivated);
        copy.GrantedTriggers.AddRange(GrantedTriggers);
        copy.BlockRestrictions.AddRange(BlockRestrictions);
        copy.Subtypes.Clear();
        copy.Subtypes.AddRange(Subtypes);
        copy.Colors.Clear();
        copy.Colors.AddRange(Colors);
        return copy;
    }

    internal ComputedCharacteristics Build() => new()
    {
        Card = Card,
        Power = Power,
        Toughness = Toughness,
        CardTypes = CardTypes,
        Keywords = Keywords,
        IsEveryCreatureType = IsEveryCreatureType,
        DoesNotUntap = DoesNotUntap,
        ControllerId = ControllerId,
        Subtypes = [.. Subtypes],
        Colors = [.. Colors],
        GoadedBy = [.. GoadedBy],
        MustAttackPlayer = MustAttackPlayer,
        CantBeBlockedByGreaterPower = CantBeBlockedByGreaterPower,
        MayAssignAsThoughUnblocked = MayAssignAsThoughUnblocked,
        MayAttackAsThoughNoDefender = MayAttackAsThoughNoDefender,
        HasLostAllAbilities = HasLostAllAbilities,
        IsLegendary = IsLegendary,
        GrantedActivated = [.. GrantedActivated],
        GrantedTriggers = [.. GrantedTriggers],
        BlockRestrictions = [.. BlockRestrictions],
        ExtraBlocks = ExtraBlocks,
        MinBlockers = MinBlockers,
        MustBeBlockedByAll = MustBeBlockedByAll,
        MustBeBlocked = MustBeBlocked,
        MustBlock = MustBlock,
        MustBlockAttacker = MustBlockAttacker,
        CantBlockAttacker = CantBlockAttacker,
        AbilitiesCantBeActivated = AbilitiesCantBeActivated,
    };
}

/// <summary>
/// A continuous effect: what it applies to, which layer it applies in, and what it does.
/// </summary>
/// <remarks>
/// Never stored in <see cref="GameState"/>. A static ability's effect exists exactly while its
/// source is on the battlefield (CR 604.2), so it is recomputed from the battlefield every time
/// rather than recorded — which is the whole fix. The previous engine had
/// <c>IStaticAbility.Apply(state) =&gt; state</c> and wrote the buff into the creature; when the
/// lord left, nothing took it back off.
/// </remarks>
public sealed record ContinuousEffectDefinition
{
    public required string Id { get; init; }

    public required EffectLayer Layer { get; init; }

    /// <summary>
    /// Whether this effect applies to the object whose characteristics are being computed.
    /// </summary>
    /// <remarks>
    /// The third argument is the target's characteristics <em>as computed so far</em>, not its
    /// printed card. That is the whole point of the layer system: an anthem that pumps white
    /// creatures has to see a creature that layer 5 turned white a moment ago, and an effect
    /// reading the printed card would miss it (CR 613.1, and the worked example under 613.5).
    /// <para>
    /// <c>source</c> is the permanent whose static ability this is, or null for an effect
    /// floating free of its source.
    /// </para>
    /// </remarks>
    public required Func<GameState, GameObject?, CharacteristicsBuilder, bool> Applies { get; init; }

    /// <summary>What it does, applied to the characteristics as they stand at its layer.</summary>
    /// <remarks>
    /// Handed the state as well as the builder, because some effects have to count something to
    /// know how much they do — "gets +1/+1 for each creature you control" cannot be a fixed pair
    /// of numbers. <see cref="Applies"/> has always been given the state for the same reason;
    /// this half was the one that could not ask.
    /// <para>
    /// And handed the <em>source</em>, for the same reason and one step further: "you" in "for
    /// each artifact you control" is whoever controls the ability, which is not always whoever
    /// controls the thing it changes. An Aura on an opponent's creature is the case that
    /// separates them, and reading the affected object's controller there counts the wrong
    /// player's board. <see cref="Applies"/> was given the source from the start; this half being
    /// without it was an asymmetry, not a design.
    /// </para>
    /// </remarks>
    public required Action<State.GameState, GameObject?, CharacteristicsBuilder> Apply { get; init; }

    /// <summary>
    /// Whether this effect takes every ability away from what it applies to (CR 613.1f).
    /// </summary>
    /// <remarks>
    /// Declared rather than done in <see cref="Apply"/>, and that is the whole point of the
    /// field: losing all abilities is two things, and only one of them is a characteristic. The
    /// keywords and the granted abilities come off the object being computed, which an
    /// <c>Apply</c> could do — but the object's own static abilities have to stop being
    /// <em>offered</em> at all, and that decision is made before any of them is applied, to a
    /// permanent that may not be the one being computed. Nothing inside an <c>Apply</c> can reach
    /// it. So <see cref="State.Characteristics"/> reads this flag on the way in, drops the
    /// affected permanent's own effects from layer 6 onwards, and calls
    /// <see cref="CharacteristicsBuilder.LoseAllAbilities"/> when the layer is reached.
    /// <para>
    /// It is also what keeps the common case free. A board with no ability-removal on it is
    /// recognised by one pass over effects that are being gathered anyway, and the layers run
    /// exactly as they did before.
    /// </para>
    /// <para>
    /// The layer must be <see cref="EffectLayer.Ability"/>; nothing else is a legal place to
    /// remove an ability, and <see cref="State.Characteristics"/> refuses one that says otherwise.
    /// </para>
    /// </remarks>
    public bool RemovesAllAbilities { get; init; }

    /// <summary>
    /// The card whose copiable values this effect grants, for a copy effect (CR 707.2).
    /// </summary>
    /// <remarks>
    /// Declared rather than done inside <see cref="Apply"/>, for the reason
    /// <see cref="RemovesAllAbilities"/> is declared: a copy is two things and only one of them
    /// is a characteristic. The values it writes an <c>Apply</c> could write — but the copied
    /// card's <em>static abilities</em> have to start being <em>offered</em>, and that decision
    /// is made in <see cref="State.Characteristics"/> before any effect is applied, while
    /// walking permanents that are not the one being computed. Nothing inside an <c>Apply</c>
    /// can reach it.
    /// <para>
    /// The whole card travels on the definition rather than an id naming an object, because
    /// CR 707.2b fixes the copiable values when the copy is made: the permanent that was copied
    /// can be in a graveyard, or gone, the next time this effect is applied, and a copy that
    /// looked its original up would stop being one.
    /// </para>
    /// <para>
    /// The layer must be <see cref="EffectLayer.Copy"/> — CR 613.2a is the only place a copy
    /// effect applies — and <see cref="State.Characteristics"/> refuses a definition that says
    /// otherwise, for the reason it refuses an ability removal outside layer 6: applied in the
    /// wrong layer it would look like a working card while getting the order wrong.
    /// </para>
    /// </remarks>
    public CardDefinition? Copies { get; init; }

    /// <summary>
    /// While this has to stay true, for an effect that lasts "for as long as ..." (CR 611.2b).
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="Applies"/>, and the difference is the rule: an
    /// effect whose condition stops being true **ends**, and does not start again if the
    /// condition becomes true once more. Folded into `Applies` it would simply stop applying and
    /// resume later, which is a different card - a creature stolen "for as long as you control
    /// this creature" would come back to you every time you regained the thief.
    /// <para>
    /// Null for the effects that have no such condition, which is nearly all of them: an
    /// until-end-of-turn effect is ended by its turn number and a permanent one never ends.
    /// </para>
    /// </remarks>
    public Func<State.GameState, IAbilitySource, bool>? While { get; init; }
}

/// <summary>
/// A replacement effect: it watches for an event and replaces it with different ones (CR 614.1).
/// </summary>
/// <remarks>
/// Replacement effects do not trigger and do not use the stack. They modify the event before it
/// ever happens, which is why an event that is replaced never triggers anything watching for it
/// (CR 603.2g) — the original event did not occur.
/// </remarks>
public sealed record ReplacementEffectDefinition
{
    public required string Id { get; init; }

    /// <summary>Whether this effect applies to the event that is about to happen.</summary>
    public required Func<GameEvent, GameState, GameObject, bool> Applies { get; init; }

    /// <summary>
    /// What happens instead. An empty list means the event simply does not happen — which is how
    /// prevention works (CR 615.1).
    /// </summary>
    public required Func<GameEvent, GameState, GameObject, IReadOnlyList<GameEvent>> Replace { get; init; }

    /// <summary>
    /// Where the source has to be for the effect to apply, or null for anywhere (CR 614.6).
    /// </summary>
    /// <remarks>
    /// Null is what "enters tapped" needs, and the reason is CR 305.1: a land is *played*, never
    /// cast, so it goes from hand straight to the battlefield and is never on the stack — while a
    /// permanent *spell* with the same words is on the stack when it resolves. Pinning the
    /// replacement to the stack meant every enters-tapped land in the game arrived untapped, and
    /// no test noticed because none of them played one.
    /// </remarks>
    public Zone? FunctionsFrom { get; init; } = Zone.Battlefield;

    /// <summary>
    /// Whether its controller may decline it (CR 614.1b) - "you may" rather than "instead".
    /// </summary>
    /// <remarks>
    /// The engine asks before applying an optional replacement, which is the one thing a
    /// replacement effect could not do until dredge needed it: the question comes up in the
    /// middle of applying an event rather than at the end of a resolution, so the event is held
    /// and re-emitted once the answer arrives - the same machinery that already asked which of
    /// two replacements to apply first.
    /// </remarks>
    public bool IsOptional { get; init; }

    /// <summary>
    /// What happens instead when an optional replacement is declined, or null for "the event".
    /// </summary>
    /// <remarks>
    /// Almost every optional replacement has two outcomes and one of them is "nothing happens
    /// differently", which is what null means. A shockland has two outcomes that are both
    /// different from the event: **"as this enters, you may pay 2 life. If you don't, it enters
    /// tapped."** Applying is paying and entering untapped; declining is entering tapped; and
    /// letting the event happen unchanged - entering untapped for free - is the one thing the
    /// card never does.
    /// <para>
    /// Only consulted when the declined question had exactly one candidate. With several, what
    /// "declining" means is a question about all of them (CR 616.1) and this cannot answer it.
    /// </para>
    /// </remarks>
    public Func<GameEvent, GameState, GameObject, IReadOnlyList<GameEvent>>? Decline { get; init; }
}
