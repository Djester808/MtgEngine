using MtgEngine.Cards;

namespace MtgEngine.Rules.Keywords;

/// <summary>
/// The keywords that change what a spell costs or when and from where it may be cast, CR 118.9
/// and CR 601.3. The casting system reads the printed <see cref="KeywordAbility.Cost"/> off the
/// face, so these are left in place rather than expanded: an alternative cost is not an
/// ability that resolves, it is a different way of paying for the same spell.
/// </summary>
public static partial class KeywordSemantics
{
    /// <summary>The alternative and optional cost keywords, with the cost each one is.</summary>
    private static IEnumerable<KeywordSemantic> CostKeywords() =>
    [
        // Timing only: the mana cost is unchanged, the window is wider, CR 601.3.
        Cost(KeywordKind.Flash, CostShape.TimingPermission, Zone.Hand,
            "May be cast any time its controller could cast an instant."),

        // Additional costs, CR 601.2b: paid with the mana cost, and the choice is locked in as
        // the spell is cast, so the payment never suspends.
        Cost(KeywordKind.Kicker, CostShape.Additional, Zone.Hand,
            "The Cost may be paid in addition as the spell is cast; abilities may look back with WasKicked."),
        Cost(KeywordKind.Buyback, CostShape.Additional, Zone.Hand,
            "The Cost may be paid in addition; if it was, the spell returns to its owner's hand instead of the graveyard."),

        // Alternative costs, CR 118.9: they replace the mana cost, and several of them also
        // move the legal cast zone, which is the second field.
        Cost(KeywordKind.Flashback, CostShape.Alternative, Zone.Graveyard,
            "Cast from the graveyard for the Cost, then exile it instead of putting it anywhere else."),
        Cost(KeywordKind.Escape, CostShape.Alternative, Zone.Graveyard,
            "Cast from the graveyard for the Cost, which includes exiling other cards from that graveyard."),
        Cost(KeywordKind.Madness, CostShape.Alternative, Zone.Exile,
            "When discarded, exile it instead and it may be cast for the Cost while it is exiled."),
        Cost(KeywordKind.Foretell, CostShape.Alternative, Zone.Exile,
            "Exiled face down from hand for {2} on a turn of its controller's, then cast for the Cost on a later turn."),
        Cost(KeywordKind.Evoke, CostShape.Alternative, Zone.Hand,
            "Cast for the Cost; a sacrifice trigger fires when the permanent enters."),
        Cost(KeywordKind.Dash, CostShape.Alternative, Zone.Hand,
            "Cast for the Cost; the creature gains haste and is returned to hand at the next end step."),
        Cost(KeywordKind.Overload, CostShape.Alternative, Zone.Hand,
            "Cast for the Cost; \"target\" in its text is replaced by \"each\", which is a text change, CR 612."),
        Cost(KeywordKind.Morph, CostShape.Alternative, Zone.Hand,
            "May be cast face down as a 2/2 for {3}; the printed Cost turns it face up any time, CR 708."),
        Cost(KeywordKind.Mutate, CostShape.Alternative, Zone.Hand,
            "Cast for the Cost onto a non-Human creature; the two become one permanent, CR 702.140."),
        Cost(KeywordKind.Prototype, CostShape.Alternative, Zone.Hand,
            "May be cast for the smaller printed Cost, which also sets its colour, size and mana value."),

        // Activated abilities whose cost is printed with the keyword, CR 602.
        Cost(KeywordKind.Equip, CostShape.Activated, Zone.Battlefield,
            "Sorcery-speed activated ability: pay the Cost, attach this Equipment to a creature you control."),
        Cost(KeywordKind.Cycling, CostShape.Activated, Zone.Hand,
            "Activated from hand: pay the Cost and discard this card, draw a card. Expanded into an ActivatedAbility."),
    ];

    private static KeywordSemantic Cost(KeywordKind keyword, CostShape shape, Zone from, string note) =>
        new(
            keyword,
            KeywordTreatment.AlternativeOrOptionalCost,
            note,
            // Cycling is the one cost keyword that is an ordinary activated ability with an
            // ordinary effect, so it is the one the expander rewrites; the rest are payments,
            // and a payment has no resolution for the interpreter to run.
            Expands: keyword == KeywordKind.Cycling,
            Cost: shape,
            CastFrom: from);
}
