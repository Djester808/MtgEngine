using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Query;

/// <summary>
/// The read-only query layer: does an object match a description, is a condition true, what
/// number is an amount, which objects does a selector name. Every characteristic comes from
/// <see cref="ICharacteristics"/> (CR 613), never from the printed face, so a filter sees the
/// object as the layers made it. Nothing here changes state, and nothing here guesses: a
/// question the state cannot answer yet throws naming the record that asked it.
/// </summary>
public sealed partial class Query(ICharacteristics characteristics) : IQuery
{
    private readonly ICharacteristics _layers = characteristics;

    public string Name => "query layer";

    /// <summary>
    /// A filter with no zone describes the battlefield, where permanents are, CR 110.1 — unless it
    /// asks for a spell, which is on the stack by definition, CR 112.1 (seam-requests/E9t.md).
    /// </summary>
    [RuleRef("112.1")]
    private static Cards.Zone ZoneOf(ObjectFilter filter) =>
        filter.In ?? (filter.Spell == true ? Cards.Zone.Stack : Cards.Zone.Battlefield);

    public bool Matches(GameState state, ObjectFilter filter, ObjectId candidate, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(filter);
        if (!state.Objects.TryGetValue(candidate, out var obj) || obj.Zone.Zone != ZoneOf(filter))
            return false;

        return MatchesAs(state, filter, obj, _layers.Of(state, candidate), context)
            && (filter.Also is null || Holds(state, filter.Also, candidate, context));
    }

    /// <summary>
    /// The half of a filter that is answered from the object and its characteristics alone. It is
    /// separate because a look-back reads an object the state no longer holds (CR 603.10, CR 111.1)
    /// against the characteristics it had, and that object has no id to hand the layers.
    /// </summary>
    private bool MatchesAs(GameState state, ObjectFilter filter, GameObject obj, Characteristics c, EffectContext context) =>
        TypesMatch(filter, c)
        && ColorsMatch(filter, c)
        && StatsMatch(state, filter, c, obj, context)
        && StatusMatch(state, filter, c, obj, context)
        && ControlMatch(state, filter, c, obj, context);

    /// <summary>Every object that could match, in a deterministic order. The zone is checked first so a filter never asks the layers about an object in the wrong zone.</summary>
    private IEnumerable<ObjectId> Candidates(GameState state, ObjectFilter filter, EffectContext context)
    {
        var zone = ZoneOf(filter);
        return state.Objects.Values
            .Where(o => o.Zone.Zone == zone)
            .OrderBy(o => o.Id.Value)
            .Select(o => o.Id)
            .Where(id => Matches(state, filter, id, context));
    }

    private static bool TypesMatch(ObjectFilter f, Characteristics c)
    {
        // CR 205.2a: an object can have several card types, so a named set must all be present.
        if (f.Types is { } all && (c.Types & all) != all)
            return false;
        if (f.AnyTypes is { } any && (c.Types & any) == 0)
            return false;
        if (f.NotTypes is { } none && (c.Types & none) != 0)
            return false;
        if (f.Supertypes is { } sup && (c.Supertypes & sup) != sup)
            return false; // CR 205.4a
        if (f.NotSupertypes is { } nosup && (c.Supertypes & nosup) != 0)
            return false;

        // CR 205.3: subtypes read as a disjunction — "Elf or Goblin", "Island or Swamp" — which
        // is how card text almost always names more than one. A conjunction of two creature types
        // is two filters to the translator, not one list.
        if (f.Subtypes is { Count: > 0 } subs && !subs.Any(s => HasSubtype(c, s)))
            return false;
        if (f.NotSubtypes is { Count: > 0 } nosubs && nosubs.Any(s => HasSubtype(c, s)))
            return false;
        // CR 205.3i: "a land with a basic land type" is any of the five, whatever else it has, and
        // is a different question from the Basic supertype (CR 205.4a) — a dual land has a basic
        // land type and is not basic. The subtypes are read after layer 4, so a land that has been
        // given a type answers yes, and one whose types were removed answers no.
        if (f.HasBasicLandType is { } basicLand && basicLand != HasAnyBasicLandType(c))
            return false;
        if (f.Historic is { } historic && historic != IsHistoric(c))
            return false;
        if (f.Outlaw is { } outlaw && outlaw != IsOutlaw(c))
            return false;
        return true;
    }

    /// <summary>
    /// CR 205.3: does this object have that subtype? An object that is every creature type
    /// (CR 205.3d, what changeling grants under CR 702.72a) has every creature type of CR 205.3m
    /// without any of them being in its list — and still has no subtype that is not a creature
    /// type, so an Equipment or a Saga is not one of them.
    /// </summary>
    [RuleRef("205.3d")]
    private static bool HasSubtype(Characteristics c, string subtype) =>
        c.Subtypes.Any(s => string.Equals(s, subtype, StringComparison.OrdinalIgnoreCase))
        || (c.EveryCreatureType && CreatureTypes.IsCreatureType(subtype));

    /// <summary>
    /// CR 205.3i: does this object have any of the five basic land types? An object that is every
    /// creature type (CR 205.3d) has none of them, because they are land types and not creature
    /// types, which <see cref="HasSubtype"/> already gets right.
    /// </summary>
    [RuleRef("205.3i")]
    private static bool HasAnyBasicLandType(Characteristics c) =>
        LandTypes.Basic.Any(basic => HasSubtype(c, basic));

    /// <summary>Historic: legendary or an artifact (CR 205.4a, 205.2a) or a Saga (CR 205.3, CR 714).</summary>
    private static bool IsHistoric(Characteristics c) =>
        (c.Supertypes & Supertype.Legendary) != 0 || (c.Types & CardType.Artifact) != 0 || HasSubtype(c, "Saga");

    /// <summary>Outlaw: an Assassin, Mercenary, Pirate, Rogue or Warlock, CR 205.3m.</summary>
    private static bool IsOutlaw(Characteristics c) =>
        HasSubtype(c, "Assassin") || HasSubtype(c, "Mercenary") || HasSubtype(c, "Pirate")
        || HasSubtype(c, "Rogue") || HasSubtype(c, "Warlock");

    private static bool ColorsMatch(ObjectFilter f, Characteristics c)
    {
        if (f.Colors is { } all && (c.Colors & all) != all)
            return false; // CR 105.1
        if (f.AnyColor is { } any && (c.Colors & any) == 0)
            return false;
        if (f.NotColors is { } none && (c.Colors & none) != 0)
            return false;

        var colors = System.Numerics.BitOperations.PopCount((uint)c.Colors);
        if (f.Colorless is { } colorless && colorless != (colors == 0))
            return false; // CR 105.2c
        if (f.Multicolored is { } multi && multi != (colors > 1))
            return false; // CR 105.2b
        if (f.Monocolored is { } mono && mono != (colors == 1))
            return false; // CR 105.2a
        return true;
    }

    private bool StatsMatch(GameState state, ObjectFilter f, Characteristics c, GameObject obj, EffectContext ctx)
    {
        if (f.Power is { } power && !Meets(c.Power, power, state, ctx))
            return false; // CR 208.1
        if (f.Toughness is { } toughness && !Meets(c.Toughness, toughness, state, ctx))
            return false; // CR 208.1
        if (f.Loyalty is { } loyalty && !Meets(c.Loyalty, loyalty, state, ctx))
            return false; // CR 306.5b
        if (f.ManaValue is { } manaValue && !Meets(ManaCosts.Value(c.ManaCost, obj), manaValue, state, ctx))
            return false; // CR 202.3
        return true;
    }

    private bool StatusMatch(GameState state, ObjectFilter f, Characteristics c, GameObject obj, EffectContext ctx)
    {
        if (f.Tapped is { } tapped && tapped != obj.Tapped)
            return false; // CR 110.5a
        if (f.FaceDown is { } down && down != obj.FaceDown)
            return false; // CR 708.2
        if (f.Token is { } token && token != obj.IsToken)
            return false; // CR 111.1
        if (f.Permanent is { } permanent && permanent != IsPermanent(obj, c))
            return false; // CR 110.1 on the battlefield, CR 110.4a elsewhere
        if (f.Spell is { } spell && spell != IsSpell(obj))
            return false; // CR 112.1: a spell is a card on the stack
        if (f.Attacking is { } attacking && attacking != IsAttacking(state, obj.Id))
            return false; // CR 506.3c
        if (f.Blocking is { } blocking && blocking != IsBlocking(state, obj.Id))
            return false; // CR 509.1a
        if (f.Blocked is { } blocked && blocked != IsBlocked(state, obj.Id))
            return false; // CR 509.1h
        // "each creature in combat": attacking (CR 506.2) or blocking (CR 506.4). The fixed fields
        // are joined with "and", so Attacking + Blocking would match nothing; this is the one
        // disjunction that gets a field of its own.
        if (f.InCombat is { } inCombat && inCombat != (IsAttacking(state, obj.Id) || IsBlocking(state, obj.Id)))
            return false; // CR 506.2, 506.4
        if (f.WithCounter is { } kind && obj.Counters.GetValueOrDefault(kind) <= 0)
            return false; // CR 122.1
        if (f.WithAnyCounter is { } anyCounter && anyCounter != obj.Counters.Values.Any(n => n > 0))
            return false;

        // "another": any object but the one asking, CR 109.1 — object identity is the id.
        if (f.Other is { } other && other != (ctx.Source is null || ctx.Source != obj.Id))
            return false;
        if (f.Named is { } named && !string.Equals(c.Name, named, StringComparison.Ordinal))
            return false; // CR 201.2
        if (f.Has is { } keyword && !c.Has(keyword))
            return false; // CR 702.1
        // CR 702.1: "creatures without flying" is the negation of the same lookup and not a subtype
        // list — a keyword granted by another effect is had, so an object that was given flying is
        // refused here exactly as one that prints it is admitted by Has.
        if (f.NotHas is { } without && c.Has(without))
            return false; // CR 702.1
        // CR 903.3: a commander is designated as the game begins and stays one in every zone it
        // visits, so the flag the zone system sets at creation travels with the new object each
        // zone change makes, CR 400.7, and reading it is the whole answer.
        if (f.IsCommander is { } commander && commander != obj.IsCommander)
            return false;

        // CR 601.3: which zone the spell was cast from, which describes the casting and not the
        // object, so only a spell on the stack can answer either field.
        if (!CastFromMatches(f, obj))
            return false;
        // CR 202.1: the printed cost read as the card prints it, not counted.
        if (f.ManaCostIs is { } costs && !ManaCostIsOneOf(costs, c.ManaCost))
            return false;
        // CR 903.4: a colour the commander's colour identity does not include.
        if (f.NotInCommanderIdentityColors is { } outside && outside != OutsideCommanderIdentity(state, c, ctx))
            return false;

        return true;
    }

    /// <summary>
    /// CR 601.3: the zone a spell was cast from, read off the stack entry the announcement wrote.
    /// <c>CastFrom</c> admits only that zone and <c>NotCastFrom</c> refuses it. A candidate that is
    /// not a spell, and a spell that was never cast at all (a copy put onto the stack, CR 707.10),
    /// was cast from nowhere and matches <em>neither</em> field: "spells cast from anywhere other
    /// than their hands" says nothing about a permanent, and admitting one would let a restriction
    /// reach objects it was never written about.
    /// </summary>
    [RuleRef("601.3")]
    private static bool CastFromMatches(ObjectFilter f, GameObject obj)
    {
        if (f.CastFrom is null && f.NotCastFrom is null)
            return true;
        if (!IsSpell(obj) || obj.Stack?.CastFrom is not { } from)
            return false;
        return (f.CastFrom is not { } only || from == only)
            && (f.NotCastFrom is not { } forbidden || from != forbidden);
    }

    /// <summary>
    /// CR 202.1: the printed mana cost as a string, compared symbol by symbol — so "{1}" is not
    /// "{W}", and the absent cost of a land is neither. The list reads "any of"; an object with no
    /// cost matches no entry, and an empty list matches nothing rather than everything.
    /// </summary>
    [RuleRef("202.1")]
    private static bool ManaCostIsOneOf(IReadOnlyList<string> costs, string? actual)
    {
        if (string.IsNullOrEmpty(actual))
            return false;

        var symbols = ManaCosts.Symbols(actual).ToList();
        return costs.Any(named => symbols.SequenceEqual(ManaCosts.Symbols(named), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// CR 903.4: does this object have a colour the commander's colour identity does not include?
    /// The identity belongs to the commander <em>card</em>, so it is read off the printed
    /// characteristics — an effect that turned the commander blue changes no card's identity — and
    /// it is the identity of the commander of the player whose effect is asking (CR 903.3), which
    /// the designation the object carries in every zone names. A colourless object has no colour
    /// outside any identity and so lies inside every one.
    /// </summary>
    [RuleRef("903.4")]
    private bool OutsideCommanderIdentity(GameState state, Characteristics c, EffectContext ctx) =>
        (c.Colors & ~CommanderIdentity(state, ctx)) != Color.Colorless;

    /// <summary>
    /// The colour identity of a player's commanders, CR 903.4: the colours of the card, of the mana
    /// symbols in its mana cost, and of the mana symbols its abilities' costs print — a partner
    /// pair is one identity, so the two are unioned. With no commander in the game the question has
    /// no answer at all and is refused naming its rule, rather than answered as "inside", which
    /// would silently admit every object in a game that has no commander.
    /// <para>
    /// Not read: a mana symbol inside an ability's <em>effect</em> ("add {G}"), because the printed
    /// text is not in the state and the vocabulary carries produced mana as a record rather than as
    /// symbols. See seam-requests/E5i.md.
    /// </para>
    /// </summary>
    [RuleRef("903.4")]
    private Color CommanderIdentity(GameState state, EffectContext ctx)
    {
        var identity = Color.Colorless;
        var found = false;
        foreach (var obj in state.Objects.Values.Where(o => o.IsCommander && o.Owner == ctx.Controller).OrderBy(o => o.Id.Value))
        {
            found = true;
            var printed = _layers.PrintedOf(obj);
            identity |= printed.Colors | ManaCosts.Colors(printed.ManaCost);
            foreach (var ability in printed.Abilities)
                identity |= AbilityCostColors(ability);
        }

        return found
            ? identity
            : throw Unanswerable(nameof(ObjectFilter.NotInCommanderIdentityColors), "this player has no commander in the game", "903.4");
    }

    /// <summary>The colours of the mana symbols an ability's costs print, CR 903.4 — part of the card's rules text.</summary>
    [RuleRef("903.4")]
    private static Color AbilityCostColors(Ability ability)
    {
        var costs = ability switch
        {
            ActivatedAbility activated => activated.Costs,
            KeywordAbility keyword => keyword.Costs,
            _ => null,
        };
        var colors = ability is KeywordAbility k ? ManaCosts.Colors(k.Cost) : Color.Colorless;
        foreach (var cost in costs ?? [])
        {
            colors |= cost switch
            {
                Cards.ManaCost mana => ManaCosts.Colors(mana.Mana),
                PayManaCost pay => ManaCosts.Colors(pay.Colored),
                _ => Color.Colorless,
            };
        }

        return colors;
    }

    private bool ControlMatch(GameState state, ObjectFilter f, Characteristics c, GameObject obj, EffectContext ctx)
    {
        // The controller is a computed characteristic, CR 613.1a, so it comes from the layers.
        if (f.ControlledBy is { } by && !Players(state, by, ctx).Contains(c.Controller))
            return false;
        if (f.OwnedBy is { } owner && !Players(state, owner, ctx).Contains(obj.Owner))
            return false; // CR 108.3
        return true;
    }

    /// <summary>CR 110.4: the six permanent types, the ones a permanent card can be put onto the battlefield as.</summary>
    private const CardType PermanentTypes = CardType.Artifact | CardType.Battle | CardType.Creature
        | CardType.Enchantment | CardType.Land | CardType.Planeswalker;

    /// <summary>
    /// What <c>permanent</c> asks of an object, which depends on where the object is. On the
    /// battlefield it is CR 110.1's question — a card or token there is a permanent whatever its
    /// types, and one that has lost them all still is (CR 110.4c). Read against a card in a
    /// library, a graveyard, a hand or exile the word can only be CR 110.4a's "permanent card": a
    /// card that could be put onto the battlefield, which is a question about its card types. A
    /// card on the stack keeps the zone's answer, because a spell there is CR 110.4b's "permanent
    /// spell" — a narrower set, no record asks for it, and guessing between the two would be the
    /// silent wrong answer this arm already gave a library.
    /// </summary>
    [RuleRef("110.4a")]
    private static bool IsPermanent(GameObject obj, Characteristics c) => obj.Zone.Zone switch
    {
        Cards.Zone.Battlefield => true,
        Cards.Zone.Stack => false,
        _ => (c.Types & PermanentTypes) != CardType.None,
    };

    /// <summary>CR 112.1: a spell is a card on the stack. An ability on the stack names the ability it came from.</summary>
    private static bool IsSpell(GameObject obj) =>
        obj.Zone.Zone == Cards.Zone.Stack && obj.Stack?.AbilityIndex is null;

    private static bool IsAttacking(GameState state, ObjectId id) =>
        state.Combat?.Attackers.ContainsKey(id) == true; // CR 506.3c

    /// <summary><see cref="CombatState.Blockers"/> maps each attacker to the creatures blocking it, CR 509.1.</summary>
    private static bool IsBlocking(GameState state, ObjectId id) =>
        state.Combat is { } combat && combat.Blockers.Values.Any(blockers => blockers.Contains(id));

    private static bool IsBlocked(GameState state, ObjectId id) =>
        state.Combat is { } combat && combat.Blockers.TryGetValue(id, out var by) && by.Count > 0; // CR 509.1h

    /// <summary>A characteristic the object does not have satisfies no comparison, CR 208.4.</summary>
    private bool Meets(int? actual, Comparison comparison, GameState state, EffectContext ctx) =>
        actual is { } value && Satisfies(value, comparison.Op, Evaluate(state, comparison.Value, ctx));

    private static bool Satisfies(int left, ComparisonOp op, int right) => op switch
    {
        ComparisonOp.Eq => left == right,
        ComparisonOp.Ne => left != right,
        ComparisonOp.Lt => left < right,
        ComparisonOp.Le => left <= right,
        ComparisonOp.Gt => left > right,
        ComparisonOp.Ge => left >= right,
        _ => throw new NotImplementedException($"Comparison operator {op} is not implemented."),
    };

    /// <summary>
    /// The one honest answer to a question the state cannot yet answer: which record asked, what
    /// is missing, and the rule the record cites. It is a <see cref="RefusedException"/> and not a
    /// <see cref="NotImplementedException"/> on purpose — <see cref="Game.Apply"/> turns it into a
    /// <see cref="Refused"/>, so a table with one such card is told which primitive is missing
    /// instead of the game dying with the command half applied.
    /// </summary>
    private static RefusedException Unanswerable(string record, string missing, string? rule = null) =>
        new($"{record} cannot be answered by the query layer yet: {missing}.", rule);
}
