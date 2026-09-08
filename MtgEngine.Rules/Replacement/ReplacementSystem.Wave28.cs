using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;

namespace MtgEngine.Rules.Replacement;

/// <summary>
/// Wave 28: what a prevention says out loud (CR 615.13), the damage no prevention can touch
/// (CR 615.12), and the two riders V15 and V16 added to the replaced events — the mana a
/// permanent was tapped for (CR 605.1a with 106.12) and the draw step's own first draw
/// (CR 121.1).
/// </summary>
public sealed partial class ReplacementSystem
{
    /// <summary>
    /// CR 615.13: a prevention effect was applied to a damage event and prevented some or all of
    /// it. The log never said so before — <see cref="Prevent"/> takes the damage out of the
    /// <c>DamageDealt</c> event and, when it prevents all of it, the event does not happen at all,
    /// so the only two facts a card of this shape triggers on were nowhere in the log.
    /// <para>
    /// It folds nothing: the <c>DamageDealt</c> that survives already folds what was dealt, and
    /// this exists so the happening is recorded. <paramref name="Prevented"/> is the amount
    /// removed from the event — <c>min(shield, amount)</c> — and never what was dealt, because
    /// the matcher publishes it verbatim as "that much" (CR 615.13).
    /// </para>
    /// <para>
    /// <b>This record is a stand-in.</b> The event belongs in <c>Events/**</c>, where E1f declares
    /// it this wave; the replacement system may not declare an event in another agent's folder, so
    /// it is declared here against <see cref="IDamagePrevented"/> — the interface the trigger
    /// matcher actually reads (<c>Triggers/TriggerMatching.Wave27.cs</c>) — and swaps for E1f's
    /// record by deleting this declaration. Nothing else changes: every emitter below names only
    /// the five members of the interface. See <c>seam-requests/E8f.md</c>.
    /// </para>
    /// </summary>
    [RuleRef("615.13")]
    public sealed record DamagePrevented(
        ObjectId Source,
        ObjectId? ToObject,
        PlayerId? ToPlayer,
        int Prevented,
        ObjectId? PreventedBy) : GameEvent, IDamagePrevented
    {
        /// <summary>CR 615.13: the prevention is a happening, not a change to the state; the surviving damage folds itself.</summary>
        public override GameState Apply(GameState state) => state;
    }

    /// <summary>
    /// CR 615.13: record what this application of a prevention effect actually took off the event.
    /// A prevention that prevented nothing — an effect applied to unpreventable damage, CR 615.12 —
    /// says nothing, because the matcher refuses a zero and a card of this shape would otherwise
    /// fire on damage that was never prevented.
    /// </summary>
    [RuleRef("615.13")]
    private static Applied Recorded(Applied applied, DamageDealt damage, int prevented, Candidate candidate)
    {
        if (prevented <= 0)
            return applied;

        var said = new DamagePrevented(damage.Source, damage.ToObject, damage.ToPlayer, prevented, candidate.Source);
        return applied with { State = said.Apply(applied.State), Extra = applied.Extra.Add(said) };
    }

    /// <summary>
    /// CR 615.12: damage that "can't be prevented". An applicable prevention effect is still
    /// applied to it — once, CR 615.12a, which the caller's <c>used</c> set already guarantees —
    /// and prevents nothing, while any additional effects that prevention has still take place.
    /// An existing shield is not reduced by it either, so the shield's own arm returns before it
    /// writes a smaller amount back.
    /// <para>
    /// The effect names the damage and not who takes it — <c>From</c> and <c>Combat</c> are its
    /// only fields (CR 615.12: "however it is dealt") — so the objects and players it was created
    /// against are not read here. It is created against every player in the game (E9v, CR 102.1)
    /// precisely because there is no recipient for it to narrow.
    /// </para>
    /// </summary>
    [RuleRef("615.12")]
    private bool Unpreventable(GameState state, DamageDealt damage)
    {
        foreach (var active in state.ActiveEffects)
        {
            if (active.Effect is not DamageCantBePrevented cant || !Combat(cant.Combat, damage))
                continue;

            if (cant.From is { } from
                && !_query.Matches(state, from, damage.Source, Context(active.Source, active.Controller, damage)))
                continue;

            return true;
        }

        return false;
    }

    /// <summary>
    /// CR 106.12 with 605.1a: "if you tap a permanent for mana". Tapping a permanent for mana is
    /// activating a mana ability of it whose cost includes {T}, so the mana this rider replaces is
    /// mana a mana ability added while that tapping was paid — which the pool's provenance names
    /// (CR 106.6): a block with a positive amount is a production, and its source is the permanent
    /// that made it, tapped by the cost before the mana was added.
    /// <para>
    /// It is not a <c>WouldTap(ForMana: true)</c>, which replaces the tapping and has no mana to
    /// add to yet, and not a <c>FromSource</c> of <c>Tapped: true</c>, which admits every source
    /// because a source tapped for mana is already tapped when the ability is activated (V16).
    /// </para>
    /// </summary>
    [RuleRef("106.12")]
    private static bool TappedForMana(GameState state, ManaChanged mana) =>
        mana.Delta.Provenance.Any(block =>
            block.Amount > 0
            && state.Objects.TryGetValue(block.Source, out var source)
            && source.Zone.Zone == Zone.Battlefield
            && source.Tapped);

    /// <summary>
    /// CR 121.1 with 500.1: the first card a player draws in their own draw step, which
    /// <c>ExceptFirstInDrawStep</c> is the printed exception to. The event is proposed and not yet
    /// folded, so the count is the draws already taken in this step and a zero means this is the
    /// one being spared — the same convention <c>FirstEachTurn</c> reads the turn's count under.
    /// <para>
    /// The step's count and not the turn's: a draw taken in the upkeep leaves the draw step's own
    /// draw still first, and a draw step whose draw was replaced away (CR 614.5) leaves the count
    /// at 0, so a later draw in that step is the first of it and is spared. It reads the same
    /// three facts as the trigger matcher's <c>SparedByTheirDrawStep</c>.
    /// </para>
    /// </summary>
    [RuleRef("121.1")]
    private static bool TheirDrawStepsFirstDraw(GameState state, PlayerId player) =>
        state.Turn.Step == Step.Draw
        && state.Turn.ActivePlayer == player
        && state.Player(player).ObjectsDrawnThisStep == 0;
}
