using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>
/// Which of the runner's filler cards a filter describes. A generated scenario has to put a body
/// on the board before it can point a spell at one, and the only bodies it may use are the
/// invented ones (<see cref="ScenarioFiller"/>) — reaching into <c>cards/</c> for a real artifact
/// would make that card's behaviour part of every other card's verdict.
/// </summary>
internal static class SmokeFillers
{
    /// <summary>How many bodies a target's filter gets: the kind it will be pointed at, and one other kind it admits.</summary>
    private const int Kinds = 2;

    /// <summary>The fillers that can stand on a battlefield, in the order a scenario would rather have them.</summary>
    private static IReadOnlyList<string> Permanents() =>
    [
        ScenarioFiller.Creature,
        ScenarioFiller.Artifact,
        ScenarioFiller.Enchantment,
        ScenarioFiller.Land,
    ];

    /// <summary>The fillers a library can be stocked with, so a search of any of the card types finds one.</summary>
    private static IReadOnlyList<string> Cards() =>
    [
        ScenarioFiller.BasicLand,
        ScenarioFiller.Creature,
        ScenarioFiller.Artifact,
        ScenarioFiller.Enchantment,
        ScenarioFiller.Spell,
        ScenarioFiller.Sorcery,
        ScenarioFiller.Land,

        // Last, so a search that says nothing about a mana value still finds what it always found;
        // these two are here for the searches that do compare one (CR 202.3).
        ScenarioFiller.CheapCreature,
        ScenarioFiller.CostlyCreature,
    ];

    /// <summary>
    /// CR 109.2: the permanents this filter describes that the board can place, best first, or
    /// nothing when it describes one the runner has no card for. Every field the board cannot
    /// honour must be null — a colour, a power, a subtype or a predicate names a permanent that
    /// would have to be invented, and inventing one is inventing a card.
    /// </summary>
    [RuleRef("109.2")]
    internal static IReadOnlyList<string> Bodies(ObjectFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        // A mana-value bound is answered by stocking a library, not by placing a body: which filler
        // to put on the battlefield would depend on the bound, and a target's is not read here.
        if (!Plain(filter) || filter.ManaValue is not null || filter.In is not (null or Zone.Battlefield) || filter.Spell is true)
            return [];
        return [.. Permanents().Where(name => Matches(filter, name)).Take(Kinds)];
    }

    /// <summary>
    /// CR 701.19: the card a library is stocked with so this search finds something, or null when
    /// the filter describes a card the runner has not got. A search that says nothing about what it
    /// wants is answered with a basic land, which is a card like any other.
    /// </summary>
    /// <param name="x">
    /// CR 601.2b: what the scenario announced for X, so "with mana value X or less" is a bound the
    /// board can satisfy. Null when the spell announced none, and a comparison against X is then a
    /// comparison the board cannot read.
    /// </param>
    [RuleRef("701.19")]
    internal static string? Findable(ObjectFilter filter, int? x = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!Plain(filter) || filter.In is not (null or Zone.Library))
            return null;
        return Cards().FirstOrDefault(name => Matches(filter, name) && CostsRight(filter.ManaValue, name, x));
    }

    /// <summary>
    /// CR 202.3: whether this filler's printed mana value satisfies the filter's comparison. No
    /// comparison is satisfied by anything; a comparison against an amount the scenario cannot put
    /// a number to is satisfied by nothing, because guessing the bound would be inventing the card.
    /// </summary>
    [RuleRef("202.3")]
    private static bool CostsRight(Comparison? comparison, string card, int? x)
    {
        if (comparison is null)
            return true;
        if (Bound(comparison.Value, x) is not { } bound || ScenarioFiller.ManaValueOf(card) is not { } value)
            return false;
        return comparison.Op switch
        {
            ComparisonOp.Eq => value == bound,
            ComparisonOp.Ne => value != bound,
            ComparisonOp.Lt => value < bound,
            ComparisonOp.Le => value <= bound,
            ComparisonOp.Gt => value > bound,
            ComparisonOp.Ge => value >= bound,
            _ => false,
        };
    }

    /// <summary>The number a comparison is against: a printed one, or the X the scenario announced (CR 601.2b).</summary>
    [RuleRef("601.2b")]
    private static int? Bound(Amount amount, int? x) => amount switch
    {
        Fixed fixedAmount => fixedAmount.N,
        X => x,
        _ => null,
    };

    /// <summary>
    /// The one kind a wipe destroys, or null when it names a class the board cannot fill. The
    /// filter must say nothing about who controls it: a wipe empties both battlefields (CR 701.7),
    /// and a scenario that placed bodies on one side only would assert half of what happened.
    /// </summary>
    [RuleRef("701.7")]
    internal static string? Wiped(ObjectFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return filter.ControlledBy is not null ? null : Bodies(filter).FirstOrDefault();
    }

    /// <summary>The fields a filler can honour, and every other field null (CR 109.2).</summary>
    private static bool Plain(ObjectFilter filter) =>
        filter == new ObjectFilter(
            In: filter.In,
            Types: filter.Types,
            AnyTypes: filter.AnyTypes,
            NotTypes: filter.NotTypes,
            Supertypes: filter.Supertypes,
            ControlledBy: filter.ControlledBy,
            Permanent: filter.Permanent,
            Spell: filter.Spell,
            ManaValue: filter.ManaValue);

    /// <summary>CR 205.1a: the printed type line answers the filter, because a filler has no ability that could change it.</summary>
    [RuleRef("205.1a")]
    private static bool Matches(ObjectFilter filter, string card)
    {
        if (ScenarioFiller.TypesOf(card) is not { } line)
            return false;
        if (filter.Types is { } all && (line.Types & all) != all)
            return false;
        if (filter.AnyTypes is { } any && (line.Types & any) == 0)
            return false;
        if (filter.NotTypes is { } none && (line.Types & none) != 0)
            return false;
        if (filter.Supertypes is { } supertypes && (line.Supertypes & supertypes) != supertypes)
            return false;
        return filter.Permanent is not { } permanent || permanent == IsPermanent(line.Types);
    }

    /// <summary>CR 205.2a: the permanent card types.</summary>
    [RuleRef("205.2a")]
    private static bool IsPermanent(CardType types) =>
        (types & (CardType.Creature | CardType.Artifact | CardType.Enchantment | CardType.Land | CardType.Planeswalker | CardType.Battle)) != 0;
}
