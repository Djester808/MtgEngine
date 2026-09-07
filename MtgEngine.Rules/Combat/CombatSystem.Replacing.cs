using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Combat;

/// <summary>
/// Where combat meets replacement and prevention, CR 614 and 616. Combat damage is proposed,
/// not performed: every event the damage step would apply is offered first and what comes back
/// is what happens. Nothing here reads a card or interprets an effect — which effects apply, in
/// what order, and what each one does to the event are all the replacement system's, and this
/// file only asks it. The same shape as <see cref="Zones.ZoneSystem"/>'s side of CR 614.1c.
/// </summary>
[RuleRef("614.1")]
public sealed partial class CombatSystem
{
    /// <summary>
    /// The replacement system, when there is one. It is a settable handle rather than a
    /// constructor argument because the two are built in the other order: the replacement system
    /// runs an "instead" effect through the interpreter, and the interpreter moves and taps
    /// permanents that are in combat. Null is the honest wave-1 answer — with no replacement
    /// system in the game no event can be replaced, and every assignment is dealt as assigned.
    /// </summary>
    [RuleRef("614.1a")]
    public IReplacementSystem? Replacements { get; set; }

    /// <summary>
    /// Offer one event to the replacement and prevention effects in force, CR 616.1, and hand back
    /// what actually happens: the event as they changed it, a different event, no event at all, or
    /// the <see cref="Events.ChoiceRequested"/> that stops the game while the affected player says
    /// which effect applies first (CR 101.4, 616.1).
    /// </summary>
    [RuleRef("616.1")]
    private Outcome Offer(GameState state, GameEvent proposed, PlayerId affected) =>
        Replacements is null
            ? new Outcome(proposed.Apply(state), [proposed])
            : Replacements.Replace(state, proposed, affected);

    /// <summary>
    /// Who chooses when more than one replacement applies to combat damage, CR 616.1: the player
    /// being dealt the damage, or the controller of the creature, planeswalker or battle being
    /// dealt it. The board read is the one the step began on, CR 510.2 — the same board every
    /// assignment was computed against — so an earlier event in the same batch cannot change who
    /// is asked about a later one. A combat damage event always has a recipient; the source's
    /// controller is only there so the reading is total.
    /// </summary>
    [RuleRef("616.1")]
    private PlayerId Affected(GameState state, DamageDealt damage) => damage switch
    {
        { ToPlayer: { } player } => player,
        { ToObject: { } hit } => _characteristics.ControllerOf(state, hit),
        _ => _characteristics.ControllerOf(state, damage.Source),
    };
}
