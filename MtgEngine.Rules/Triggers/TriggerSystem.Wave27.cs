using MtgEngine.Cards;

namespace MtgEngine.Rules.Triggers;

/// <summary>
/// The keys wave 27's firings carry, beside the damage and the two life amounts of CR 603.10. Each
/// is a public const so the query layer's reader and the matcher that writes it name one string in
/// one place; the readers themselves are in <c>Query/**</c> and are asked for in
/// <c>seam-requests/E6j.md</c>.
/// </summary>
public sealed partial class TriggerSystem
{
    /// <summary>
    /// The key the damage a prevention effect actually *prevented* is carried under, CR 615.13 with
    /// CR 603.10. The reader is the <c>TriggeringPreventedDamage</c> amount.
    /// <para>
    /// It is a second key and never <see cref="TriggeringDamageAmount"/>, because dealt and
    /// prevented are two different numbers on one event: a shield that prevents 2 of 5 leaves 3
    /// dealt, and full prevention leaves no damage event at all, so an effect that read the dealt
    /// amount for "that much" would deal the wrong number or nothing. A firing on a damage event
    /// writes only the dealt key and a firing on a prevention only this one, so neither reader can
    /// ever answer with the other's number.
    /// </para>
    /// </summary>
    [RuleRef("615.13")]
    public const string TriggeringPreventedDamageAmount = "triggering-prevented-damage";

    /// <summary>
    /// The key the *total* mana spent to cast the triggering spell is carried under, CR 400.7d: an
    /// ability may reference what was paid to cast a spell, and by the time a trigger that watched
    /// the cast resolves that spell may have resolved and ceased to exist (CR 400.7), so the number
    /// rides on the firing rather than being re-read off a stack entry that has gone.
    /// <para>
    /// It is written on <b>every</b> firing whose event is a spell being cast, including as "0",
    /// which is the whole point of CR 400.7d's commonest sentence: "if no mana was spent to cast
    /// it" is this amount compared against zero, and a key that were absent when nothing was paid
    /// would be indistinguishable from a firing that watched no spell at all. The total is the sum
    /// of the stack entry's per-colour payment, which is exactly what <c>ManaSpentOnThis</c> sums,
    /// so the two amounts answer the same way about the same payment and differ only in whose spell
    /// they are about.
    /// </para>
    /// </summary>
    [RuleRef("400.7d")]
    public const string ManaSpentOnTriggeringAmount = "mana-spent-on-triggering";

    /// <summary>
    /// The prefix the per-colour half of <see cref="ManaSpentOnTriggeringAmount"/> is carried
    /// under, CR 400.7d: the key is this prefix followed by the <c>Color</c> enum name, and the
    /// value is how much mana of that colour was spent. A colour nobody paid has no key rather than
    /// a zero, so "for each colour of mana spent" counts the keys and never has to subtract.
    /// </summary>
    [RuleRef("400.7d")]
    public const string ManaSpentOnTriggeringColorPrefix = "mana-spent-on-triggering-";
}
