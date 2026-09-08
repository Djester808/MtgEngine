using MtgEngine.Cards;

namespace MtgEngine.Rules.Query;

/// <summary>
/// The one public reading of CR 605.1a, for every subsystem that has to know whether an activated
/// ability is a mana ability: the runner (which offers it and counts it), the casting system (which
/// lets it be activated while a spell is being cast, CR 601.2g) and the query layer (which answers
/// <c>HasAbilityOfKind(Mana)</c>). Three private copies of the predicate disagreeing is worse than
/// no copy at all — the number a scenario is written against is computed by one of them and what
/// resolves is decided by another (seam-requests/R1.md § 2, seam-requests/R2.md § 4).
/// <para>
/// The rule names <em>four</em> criteria and every copy checked at most three. The fourth — its
/// cost and effect don't move any card to or from a library — is the one an ability that adds mana
/// and searches a library turns on, and it is read here so no caller has to remember it.
/// </para>
/// </summary>
public static class ManaAbilities
{
    /// <summary>
    /// CR 605.1a: an activated ability is a mana ability if it doesn't require a target (CR 115.6),
    /// it could add mana when it resolves, it is not a loyalty ability (CR 606.1), and its cost and
    /// effect don't move any card to or from a library. All four, or it is an ordinary activated
    /// ability that uses the stack.
    /// </summary>
    [RuleRef("605.1a")]
    public static bool Is(ActivatedAbility ability)
    {
        ArgumentNullException.ThrowIfNull(ability);
        return !RequiresTarget(ability)
            && CouldAddMana(ability.Do, null)
            && !IsLoyalty(ability)
            && !MovesACardToOrFromALibrary(ability);
    }

    /// <summary>
    /// CR 115.6: an ability requires a target when its own text says "target". Both spellings count
    /// — the object <see cref="Target"/> and the player <see cref="TargetPlayer"/> — and "up to one
    /// target" is still targeting, so an ability that can legally choose none of them is no more a
    /// mana ability than one that must choose one.
    /// </summary>
    [RuleRef("115.6")]
    public static bool RequiresTarget(ActivatedAbility ability)
    {
        ArgumentNullException.ThrowIfNull(ability);
        return Nodes(ability).Any(node => node is Target or TargetPlayer);
    }

    /// <summary>CR 606.1: a loyalty ability has a loyalty symbol in its cost.</summary>
    [RuleRef("606.1")]
    public static bool IsLoyalty(ActivatedAbility ability)
    {
        ArgumentNullException.ThrowIfNull(ability);
        return ability.Costs.OfType<LoyaltyCost>().Any();
    }

    /// <summary>
    /// Whether an effect tree could add mana when it resolves, CR 605.1a with 106.1, optionally of
    /// one of the given colors. "Could" is the word the rule uses: a conditional or optional branch
    /// that adds mana counts, because the ability is a mana ability even when the game state will
    /// not let it produce any (CR 605.2).
    /// </summary>
    [RuleRef("605.1a")]
    public static bool CouldAddMana(Effect effect, Color? colors)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return effect switch
        {
            AddMana mana => colors is null || AddsColor(mana.Mana, colors.Value),
            Sequence sequence => sequence.Steps.Any(s => CouldAddMana(s, colors)),
            If branch => CouldAddMana(branch.Then, colors) || (branch.Else is not null && CouldAddMana(branch.Else, colors)),
            May may => CouldAddMana(may.Do, colors),
            _ => false,
        };
    }

    /// <summary>
    /// CR 106.1 with 106.4: whether a production could add mana of one of the given colors. A
    /// production that names no fixed colour — any colour, any type, a chosen colour, a commander's
    /// identity, the colour of what this is attached to, whatever a land could produce — could add
    /// any of them.
    /// </summary>
    [RuleRef("106.1")]
    public static bool AddsColor(ManaProduction production, Color colors)
    {
        ArgumentNullException.ThrowIfNull(production);
        if (production.AnyColor || production.AnyType || production.ChosenColor || production.CommanderIdentity
            || production.ColorOfAttachedOrTarget || production.LandCouldProduce)
        {
            return true;
        }

        if (production.OneOf is { } oneOf && (oneOf & colors) != 0)
            return true;
        return production.Fixed is { } fixedMana && ManaCosts.Devotion(fixedMana, colors) > 0;
    }

    /// <summary>
    /// The fourth criterion of CR 605.1a: whether the ability's cost or effect moves any card to or
    /// from a library. "Add {G}. Search your library for a basic land card and put it into your
    /// hand" adds mana and is not a mana ability, and every copy of the predicate this one replaces
    /// answered that it was. The reading is the printed verbs that move a card across a library's
    /// border, plus any record that names <see cref="Zone.Library"/> as a zone at all — a
    /// destination, a source filter, a shuffle-in — so a new record naming the library is caught by
    /// the zone arm before anyone remembers to add it to the list.
    /// </summary>
    [RuleRef("605.1a")]
    public static bool MovesACardToOrFromALibrary(ActivatedAbility ability)
    {
        ArgumentNullException.ThrowIfNull(ability);
        return Nodes(ability).Any(MovesLibraryCards);
    }

    private static bool MovesLibraryCards(object node) => node switch
    {
        // The verbs that cross a library's border with no zone written on them: drawing and milling
        // (CR 121.1, 701.13a), surveilling and exploring (CR 701.42a, 701.40a), a search that puts
        // what it finds anywhere (CR 701.19a), and revealing from the top until something is found.
        Draw or Mill or Surveil or Explore or Search or RevealUntil or MillCost => true,
        Shuffle shuffle => shuffle.IntoLibrary is not null, // CR 701.20a: cards put into the library first
        _ => NamesTheLibrary(node),
    };

    /// <summary>Whether a record names the library in any <see cref="Zone"/>-typed property of its own.</summary>
    private static bool NamesTheLibrary(object node) =>
        node.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && (p.PropertyType == typeof(Zone) || p.PropertyType == typeof(Zone?)))
            .Any(p => p.GetValue(node) as Zone? == Zone.Library);

    /// <summary>
    /// Every vocabulary record the ability's cost and effect reach, which is what both remaining
    /// criteria are asked of. <see cref="ActivatedAbility.Where"/> is the zone the ability is
    /// activated <em>from</em> and not a card moving anywhere, so the walk starts below it.
    /// </summary>
    private static IEnumerable<object> Nodes(ActivatedAbility ability) =>
        ability.Costs.SelectMany(Linter.Walk).Concat(Linter.Walk(ability.Do));
}
