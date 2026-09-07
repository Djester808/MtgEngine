using MtgEngine.Cards;

namespace MtgEngine.Rules.Keywords;

/// <summary>
/// The keywords combat and the layer system read directly off computed characteristics,
/// CR 613.1f and CR 509.1b. None of these expands: combat already asks
/// <c>Characteristics.Has(kind)</c>, so rewriting them into abilities would give the rule two
/// writers, which the plan forbids.
/// </summary>
public static partial class KeywordSemantics
{
    /// <summary>The evasion, damage and protection keywords, CR 702.2–702.28 and 702.104.</summary>
    private static IEnumerable<KeywordSemantic> Evasion() =>
    [
        // Blocking restrictions, CR 509.1b — CombatSystem.CheckEvasion reads each of these.
        Combat(KeywordKind.Flying, "Can't be blocked except by creatures with flying or reach."),
        Combat(KeywordKind.Reach, "May block a creature with flying."),
        Combat(KeywordKind.Menace, "Can't be blocked except by two or more creatures."),
        Combat(KeywordKind.Fear, "Can't be blocked except by artifact creatures and/or black creatures."),
        Combat(KeywordKind.Intimidate, "Can't be blocked except by artifact creatures and/or creatures that share a colour with it."),
        Combat(KeywordKind.Shadow, "Can block or be blocked only by creatures with shadow."),
        Combat(KeywordKind.Skulk, "Can't be blocked by creatures with greater power."),
        Combat(KeywordKind.Horsemanship, "Can't be blocked by creatures without horsemanship."),
        Combat(KeywordKind.Landwalk, "Can't be blocked if the defending player controls a land of the named type; the type is the keyword's Of filter."),

        // Damage, CR 510.1 — CombatSystem's damage assignment and the SBA layer read these.
        Combat(KeywordKind.Trample, "Assigns excess combat damage to the defending player or planeswalker."),
        Combat(KeywordKind.FirstStrike, "Deals combat damage in the first-strike damage step, CR 510.4."),
        Combat(KeywordKind.DoubleStrike, "Deals combat damage in both damage steps, CR 510.4."),
        Combat(KeywordKind.Deathtouch, "Any nonzero damage from this source is lethal, CR 704.5h; one damage suffices when assigning."),
        Combat(KeywordKind.Lifelink, "Damage dealt by this source also causes its controller to gain that much life."),

        // Permanent-level statics the turn, combat and SBA systems read, CR 613.1f.
        Combat(KeywordKind.Vigilance, "Attacking does not cause this creature to tap, CR 508.1g."),
        Combat(KeywordKind.Defender, "Can't attack, CR 508.1a."),
        Combat(KeywordKind.Haste, "Ignores summoning sickness for attacking and for tap abilities, CR 302.6."),
        Combat(KeywordKind.Indestructible, "Lethal damage and destruction do not destroy it, CR 700.4 and 704.5g."),

        // Targeting restrictions, CR 115.6 — the query layer reads these when a target is chosen.
        Combat(KeywordKind.Hexproof, "Can't be the target of spells or abilities opponents control; the Of filter narrows it (\"hexproof from red\")."),
        Combat(KeywordKind.Shroud, "Can't be the target of any spell or ability."),
        Combat(KeywordKind.Ward, "The keyword's Cost is an additional cost an opponent pays or the spell or ability is countered."),
        Combat(KeywordKind.Protection, "Can't be damaged, enchanted, equipped, blocked or targeted by the quality in the Of filter, CR 702.16b."),
    ];

    private static KeywordSemantic Combat(KeywordKind keyword, string note) =>
        new(keyword, KeywordTreatment.CombatEvasion, note);
}
