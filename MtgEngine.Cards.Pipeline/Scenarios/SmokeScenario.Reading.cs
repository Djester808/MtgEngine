using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>
/// The readings every arm shares: what a record says a number is, which filler a filter
/// describes, whose permanent it is, and how a sequence comes apart. Nothing here asserts
/// anything — an arm decides what a play shows, and these decide what the record said.
/// </summary>
public static partial class SmokeScenario
{
    /// <summary>
    /// CR 601.2b: a printed number, or the X the cast announced when the record says X; null for
    /// an amount the board cannot put a number to, which then asserts nothing.
    /// </summary>
    [RuleRef("601.2b")]
    private static int? Number(SmokeBoard board, Amount amount) => amount switch
    {
        Fixed printed => printed.N,
        X => board.AnnouncedX,
        // CR 202.3: "that card's mana value" is the printed cost of the one filler a step remembered.
        ManaValueOf { Of: Remembered { Key: var key } } when board.Recalled(key) is [var (_, card)] => ScenarioFiller.ManaValueOf(card),
        _ => null,
    };

    /// <summary>A count the board can supply: the caster's own creatures, and nothing else about them.</summary>
    private static bool Countable(ObjectFilter filter) =>
        Simple(filter) && filter.Types == CardType.Creature && filter.ControlledBy is You && filter.In is null or Zone.Battlefield;

    /// <summary>
    /// Every field the board cannot honour is null: a filter with a colour, a power or a predicate
    /// describes a permanent the runner has no card for, and guessing one would be inventing a card.
    /// </summary>
    private static bool Simple(ObjectFilter filter) =>
        filter == new ObjectFilter(In: filter.In, Types: filter.Types, ControlledBy: filter.ControlledBy, Spell: filter.Spell);

    private static bool Mine(Target target) => target.Filter.ControlledBy is You;

    private static string Owner(Target target) => Mine(target) ? Caster : Opponent;

    /// <summary>CR 205.2a: the permanent types. A spell of any of them ends up on the battlefield.</summary>
    [RuleRef("205.2a")]
    private static bool Permanent(CardType types) =>
        (types & (CardType.Creature | CardType.Artifact | CardType.Enchantment | CardType.Planeswalker | CardType.Battle)) != 0;

    /// <summary>A sequence is its parts; anything conditional is skipped, because a smoke scenario asserts only what always happens.</summary>
    private static IEnumerable<Effect> Flatten(Effect effect) =>
        effect is Sequence sequence ? sequence.Steps.SelectMany(Flatten) : [effect];

    /// <summary>
    /// Every <see cref="Unsupported"/> marker inside one node — one ability, one face or a whole
    /// definition — counted by identity, because the reflective walk reaches a face's abilities
    /// twice (<see cref="Face.Abilities"/> and the <see cref="Face.AbilityList"/> that reads them
    /// back) and two markers that merely compare equal are still two clauses nobody wrote.
    /// </summary>
    internal static IReadOnlyList<Unsupported> Markers(object node) =>
        [.. Linter.Walk(node).Distinct(ReferenceEqualityComparer.Instance).OfType<Unsupported>()];

    /// <summary>
    /// CR 602.2: an activated ability is a separate object from the spell that put its source on the
    /// battlefield, so a marker on one of a face's activated abilities says nothing about the card's
    /// own resolution. A marker anywhere else on the face — in a spell ability, a trigger, a static
    /// ability or the printed data itself — is part of what casting the card does, and the cast
    /// scenario is refused rather than asserting a resolution half of which is not written down.
    /// </summary>
    [RuleRef("602.2")]
    internal static bool CastIsExpressible(Face face)
    {
        ArgumentNullException.ThrowIfNull(face);
        var inActivations = face.AbilityList.OfType<ActivatedAbility>().Sum(a => Markers(a).Count);
        return Markers(face).Count == inActivations;
    }
}
