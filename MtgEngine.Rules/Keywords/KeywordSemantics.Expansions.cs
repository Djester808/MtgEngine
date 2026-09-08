using MtgEngine.Cards;

namespace MtgEngine.Rules.Keywords;

/// <summary>
/// The keywords that are ordinary abilities in disguise, CR 702.1a: each is shorthand for a
/// triggered, activated or static ability the triggers subsystem and the interpreter already
/// understand. <see cref="KeywordExpander"/> writes the concrete record; the entry here says
/// which shape it takes and what it says.
/// </summary>
public static partial class KeywordSemantics
{
    /// <summary>Keywords that stand for a triggered ability, plus unearth, which is activated.</summary>
    private static IEnumerable<KeywordSemantic> TriggeredExpansions() =>
    [
        Expansion(
            KeywordKind.Prowess,
            KeywordTreatment.TriggeredAbilityExpansion,
            "Whenever you cast a noncreature spell, this creature gets +1/+1 until end of turn."),
        Expansion(
            KeywordKind.Exalted,
            KeywordTreatment.TriggeredAbilityExpansion,
            "Whenever a creature you control attacks alone, that creature gets +1/+1 until end of turn."),
        Expansion(
            KeywordKind.Unearth,
            KeywordTreatment.TriggeredAbilityExpansion,
            "Activated from the graveyard for the Cost at sorcery speed: return this card to the battlefield, it gains haste, exile it at the beginning of the next end step."),
        Expansion(
            KeywordKind.Cascade,
            KeywordTreatment.TriggeredAbilityExpansion,
            "When you cast this spell, exile cards from the top of your library until you exile a nonland card with lesser mana value, then you may cast it without paying its mana cost."),
        Expansion(
            KeywordKind.Riot,
            KeywordTreatment.TriggeredAbilityExpansion,
            "This permanent enters with your choice of a +1/+1 counter or haste; CR 702.136a makes the choice as it enters, so it wants a replacement, not a trigger."),
        Expansion(
            KeywordKind.Storm,
            KeywordTreatment.TriggeredAbilityExpansion,
            "When you cast this spell, copy it for each other spell that was cast before it this turn; you may choose new targets for the copies (CR 702.40a with 707.10)."),
        Expansion(
            KeywordKind.Afflict,
            KeywordTreatment.TriggeredAbilityExpansion,
            "Whenever this creature becomes blocked, defending player loses N life."),
        Expansion(
            KeywordKind.Mentor,
            KeywordTreatment.TriggeredAbilityExpansion,
            "Whenever this creature attacks, put a +1/+1 counter on target attacking creature with lesser power."),
        Expansion(
            KeywordKind.Undying,
            KeywordTreatment.TriggeredAbilityExpansion,
            "When this creature dies, if it had no +1/+1 counter on it, return it to the battlefield under its owner's control with a +1/+1 counter on it."),
        Expansion(
            KeywordKind.Persist,
            KeywordTreatment.TriggeredAbilityExpansion,
            "When this creature dies, if it had no -1/-1 counter on it, return it to the battlefield under its owner's control with a -1/-1 counter on it."),
    ];

    /// <summary>Keywords that stand for a static ability, CR 604.</summary>
    private static IEnumerable<KeywordSemantic> StaticExpansions() =>
    [
        Expansion(
            KeywordKind.Devoid,
            KeywordTreatment.StaticExpansion,
            "This object is colourless, CR 702.113a: a characteristic-defining ability applied in layer 5."),
        Expansion(
            KeywordKind.Changeling,
            KeywordTreatment.StaticExpansion,
            "This object is every creature type, CR 702.72a: a characteristic-defining ability applied in layer 4."),
        Expansion(
            KeywordKind.Affinity,
            KeywordTreatment.StaticExpansion,
            "This spell costs {1} less to cast for each permanent matching the Of filter, CR 702.40a: a cost-reduction static that applies while it is on the stack."),

        // Convoke, improvise and delve are statics too, but the thing they change is how the
        // generic part of a cost is paid, and only the casting system can do that as it builds
        // the payment. Expanding them into a StaticAbility would put the rule in two places.
        NotExpanded(
            KeywordKind.Convoke,
            "Each creature tapped while casting this spell pays for {1} or one mana of that creature's colour, CR 702.51a. Casting reads it and offers TapUntappedCost."),
        NotExpanded(
            KeywordKind.Improvise,
            "Each artifact tapped while casting this spell pays for {1}, CR 702.126a. Casting reads it and offers TapUntappedCost."),
        NotExpanded(
            KeywordKind.Delve,
            "Each card exiled from your graveyard while casting this spell pays for {1}, CR 702.66a. Casting reads it as an exile payment."),
    ];

    private static KeywordSemantic Expansion(KeywordKind keyword, KeywordTreatment treatment, string note) =>
        new(keyword, treatment, note, Expands: true);

    private static KeywordSemantic NotExpanded(KeywordKind keyword, string note) =>
        new(keyword, KeywordTreatment.StaticExpansion, note);
}
