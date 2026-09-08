using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Replacement;

/// <summary>
/// Wave 30: the two readings the replacement road owes the V18 vocabulary — which recipient a
/// damage replacement is written about (CR 614.1), and the designation a permanent enters with
/// (CR 722.3a with 614.1c).
/// </summary>
public sealed partial class ReplacementSystem
{
    /// <summary>
    /// CR 614.1 with 615: "if a source you control would deal damage to an opponent or a permanent
    /// an opponent controls" — one replacement effect whose recipient may be an object or a player.
    /// <para>
    /// The two riders are a union and not an intersection, because a damage event has exactly one
    /// recipient (CR 119.3 for a player, 120.3 for a permanent): <see cref="WouldDealDamage.To"/>
    /// admits the object half, <see cref="WouldDealDamage.ToPlayer"/> the player half, and both
    /// together are the printed "or". Reading them as an intersection would match nothing at all,
    /// since no one event is both.
    /// </para>
    /// <para>
    /// Neither rider means every recipient. That default is load-bearing rather than tidy: every
    /// stored card carrying a bare <see cref="WouldDealDamage"/> was written before the fields
    /// existed and means "damage from this source, wherever it lands" (seam-requests/V18.md row 9).
    /// A reading that defaulted to "matches nothing" would silently stop every one of them.
    /// </para>
    /// </summary>
    [RuleRef("614.1")]
    private bool Recipient(GameState state, WouldDealDamage watched, DamageDealt damage, EffectContext context)
    {
        if (watched.To is null && watched.ToPlayer is null)
            return true;

        if (watched.To is { } what && damage.ToObject is { } hit && _query.Matches(state, what, hit, context))
            return true;

        return watched.ToPlayer is { } who
            && damage.ToPlayer is { } player
            && _query.PlayerMatches(state, who, player, context);
    }

    /// <summary>
    /// CR 722.3a with 614.1c: "this creature enters prepared" gives the arriving permanent the
    /// prepared designation as part of its entry, so the designation is on the object the move
    /// carries and not put there by a later event.
    /// <para>
    /// A permanent can't gain the designation unless it has a prepare spell — the inset frame of a
    /// preparation card, which is a face of that card with <see cref="FaceRole.PrepareSpell"/>
    /// (CR 722.2a) — and one that can't is a permanent that enters unchanged rather than an error.
    /// The rule's second refusal, that a permanent already prepared can't gain it again, has
    /// nothing to refuse on this road: a permanent entering is a new object (CR 400.7) and the
    /// designation is a set, so gaining it twice is not observable until CR 722.3c's copy exists
    /// and the question becomes how many copies were made.
    /// </para>
    /// <para>
    /// What this does <em>not</em> do is CR 722.3c's copy in exile: as the designation is gained
    /// the controller creates a copy of the object in exile carrying only the prepare spell's
    /// characteristics, that copy stays there while the permanent is prepared (an exception to
    /// CR 704.5e), the controller may cast it, and the permanent loses the designation as the
    /// spell becomes cast (CR 601.2i). That is a second object in a second zone, which the
    /// replacement road cannot create — it may only modify the event it was offered. It belongs to
    /// the zone system beside its other copy road (seam-requests/E8h.md).
    /// </para>
    /// </summary>
    [RuleRef("722.3a")]
    private static GameObject Prepared(GameObject obj) =>
        HasPrepareSpell(obj)
            ? obj with { Designations = obj.Designations.Add(Query.Designations.Prepared) }
            : obj;

    /// <summary>
    /// CR 722.2a: an object has a prepare spell when the alternative characteristics of a
    /// preparation card's inset frame exist for it, which is a face of its own on the card.
    /// </summary>
    [RuleRef("722.2a")]
    private static bool HasPrepareSpell(GameObject obj) =>
        obj.Definition.Faces.Any(face => face.Role == FaceRole.PrepareSpell);
}
