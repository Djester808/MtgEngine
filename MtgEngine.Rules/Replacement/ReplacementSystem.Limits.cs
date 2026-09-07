using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Replacement;

/// <summary>
/// The prohibitions a player carries in their own right, CR 613.10, read where the event that
/// would break one is still only proposed.
/// <para>
/// These are not replacement effects and not prevention shields: nothing chooses them, nothing
/// orders them against anything else (CR 616.1 asks the affected player only when two effects
/// compete, and a "can't" is not one of the competitors), and they never run out the way a
/// shield of CR 615.7 does. "Can't" beats "does" (CR 101.2), so an event a limit forbids is
/// dropped before any card-driven candidate is gathered, and no effect is offered it at all.
/// </para>
/// </summary>
[RuleRef("613.10")]
public sealed partial class ReplacementSystem
{
    /// <summary>
    /// Whether this player's limits forbid the proposed event outright. Damage to an object is
    /// not here: an object's protection and its "prevent all damage that would be dealt to it"
    /// are card text, and reach the same loop as ordinary candidates through
    /// <see cref="WouldBeDealtDamage"/>. A player is not an object and has no such ability, so
    /// the player-scoped form (CR 702.16j) is read off <see cref="PlayerLimits"/> instead — one
    /// reading for both, because <see cref="PlayerLimits.CantBeDealtDamage"/> is already the
    /// union of the flag and protection from everything.
    /// </summary>
    [RuleRef("120.3")]
    private bool Forbidden(GameState state, GameEvent proposed) => proposed switch
    {
        // CR 120.3 for "can't be dealt damage", CR 702.16j for protection from everything:
        // all damage that would be dealt to such a player is prevented.
        DamageDealt { ToPlayer: { } player } => _layers.PlayerRestrictions(state, player).CantBeDealtDamage,

        LifeChanged life => Unchangeable(_layers.PlayerRestrictions(state, life.Player), life.Delta),

        // Targeting is not judged here. CR 115.6 and 115.7 are checked where targets are chosen
        // and rechecked on resolution; by the time an event is proposed the target is settled.
        _ => false,
    };

    /// <summary>
    /// CR 119.3 with CR 101.2: a life total that can't change can't change in either direction,
    /// so a loss is dropped as readily as a gain. "Can't gain life" is the weaker one — it stops
    /// only an increase, and life loss still happens.
    /// </summary>
    [RuleRef("119.3")]
    private static bool Unchangeable(PlayerLimits limits, int delta) =>
        limits.LifeTotalCantChange || (delta > 0 && limits.CantGainLifeNow);
}
