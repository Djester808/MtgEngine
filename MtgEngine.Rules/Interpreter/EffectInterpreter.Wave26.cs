using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// The two wave-25 vocabulary readers this folder owes (V14): the defending player a token put
/// onto the battlefield attacking is attacking (CR 508.4), and the players a permission-shaped
/// continuous effect is granted to (CR 702.8). Both are written from the rule: the token is never
/// declared as an attacker, and the permission names a player because there is no object in a
/// hand for it to reach.
/// </summary>
public sealed partial class EffectInterpreter
{
    /// <summary>
    /// CR 508.4: an effect that puts a token onto the battlefield attacking says which player — or
    /// which planeswalker that player controls — it is attacking, and the token's controller
    /// chooses among that player's side of the board. The record names the player half, so the
    /// tokens that just arrived are set attacking that player rather than joining whatever the
    /// already-declared attackers were declared against (CR 508.1). Nothing about the declaration
    /// happens again: no attack trigger fires and no attack cost is paid (CR 508.4 with 506.3c).
    /// <para>
    /// A reference that names nobody who could be attacked — nobody at all, or only the token's
    /// own controller, who is never a defending player (CR 506.2) — is refused with the rule
    /// rather than quietly leaving the token attacking whoever happened to be first.
    /// </para>
    /// </summary>
    [RuleRef("508.4")]
    private EffectRun Attacking(EffectRun run, IReadOnlyCollection<ObjectId> tokens, PlayerRef whom)
    {
        if (tokens.Count == 0)
            return run;

        // CR 506.2: the defending player is a player the attacking player is attacking, so the
        // token's own controller is never one of them, and neither is a player who has left.
        var controller = run.Context.Controller;
        var defending = Players(run, whom)
            .Where(p => p != controller)
            .Where(p => run.State.Players.Any(seat => seat.Id == p && !seat.HasLost))
            .ToImmutableList();
        if (defending.Count == 0)
        {
            throw new RefusedException(
                "a token was to enter the battlefield attacking a player the effect names, and the reference names no player who could be attacked",
                "508.4");
        }

        // CR 508.4 with 506.3c: with no combat happening there is nothing for the token to join,
        // and the effect's instruction to attack does nothing at all — as it does for a token the
        // record says only "attacking" of.
        if (run.State.Combat is not { } combat)
            return run;

        var target = new CombatTarget(defending[0], null);
        var attackers = combat.Attackers;
        foreach (var id in tokens)
        {
            if (run.State.Objects.ContainsKey(id))
                attackers = attackers.SetItem(id, target);
        }

        return Emit(run, new AttackersDeclared(combat.AttackingPlayer, attackers));
    }

    /// <summary>
    /// CR 702.8: the players a permission-shaped continuous effect is granted to. "You may cast
    /// creature spells as though they had flash" reaches no object — the card it is about is in a
    /// hand, and a spell is not a permanent — so the effect names its own player and applies to
    /// them, whichever <c>Apply</c> record the card spelled it inside. Every other continuous
    /// effect answers null and applies exactly where the <c>Apply</c> says it does.
    /// </summary>
    [RuleRef("702.8")]
    private ImmutableList<PlayerId>? Permitted(EffectRun run, ContinuousEffect what) => what switch
    {
        MayCastAsThoughFlash flash => Players(run, flash.Who),

        // CR 401.5: "play with the top card of your library revealed" is about a library, so the
        // effect is created against the player whose library it is; every player sees the card
        // (CR 401.5 with 401.6), which is the view's half and not this one's.
        PlayWithTopCardRevealed revealed => Players(run, revealed.Who),

        // CR 615.12: damage that can't be prevented names no object and no player — it is about
        // damage however and to whomever it is dealt — so it is created against every player in
        // the game (CR 102.1) rather than vanishing for want of an object the Apply could reach.
        DamageCantBePrevented => [.. run.State.Players.Select(seat => seat.Id)],
        _ => null,
    };
}
