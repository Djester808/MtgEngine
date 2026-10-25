using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Zones;

/// <summary>
/// Where the zone system meets replacement and prevention, CR 614 and 616. Every event this
/// subsystem would apply is offered first, and what comes back is what happens. Nothing here
/// reads a card or interprets an effect: which effects apply, in what order, and what each one
/// does to the event are all the replacement system's, and this file only asks it.
/// </summary>
[RuleRef("614.1")]
public sealed partial class ZoneSystem
{
    /// <summary>
    /// The replacement system, when there is one. It is a settable handle rather than a
    /// constructor argument because the two are built in the other order: the replacement system
    /// runs an "instead" effect through the interpreter, the interpreter moves cards through this
    /// one, so the composition builds the zones first and sets this once the rest exists — the
    /// same shape as <see cref="ForwardingInterpreter"/>. Null is the honest wave-1 answer: with
    /// no replacement system in the game, no event can be replaced, and every event stands.
    /// </summary>
    [RuleRef("614.1")]
    public IReplacementSystem? Replacements { get; set; }
    public ICharacteristics? Characteristics { get; init; }

    /// <summary>
    /// Offer one event to the replacement and prevention effects in force, CR 616.1, and hand back
    /// what actually happens: the event as they changed it, a different event, no event at all, or
    /// the <see cref="Events.ChoiceRequested"/> that stops the game while the affected player says
    /// which effect applies first (CR 101.4, 616.1).
    /// </summary>
    [RuleRef("616.1")]
    private ImmutableList<GameEvent> Offer(GameState state, GameEvent proposed, PlayerId affected, IEnumerable<string>? usedReplacements = null)
    {
        if (proposed is ObjectMoved move)
            proposed = move with { LibraryDeparture = Library.LibraryDepartureObservation.Capture(state, move, Characteristics) };
        return Replacements is null ? [proposed] : Replacements.Replace(state, proposed, affected, usedReplacements).Events;
    }

    /// <summary>
    /// Who chooses when more than one replacement applies to a zone change, CR 616.1: the affected
    /// object's controller. A permanent arriving is affected as the object it is about to be, so
    /// the controller it would have is the one who chooses (CR 614.12); anywhere else the object
    /// that is leaving is the affected one, and the controller it had is who chooses.
    /// </summary>
    [RuleRef("616.1")]
    private static PlayerId AffectedBy(GameState state, ObjectMoved proposed) =>
        proposed.To.Zone.Zone == Cards.Zone.Battlefield || !state.Objects.TryGetValue(proposed.From, out var moving)
            ? proposed.To.InitialController
            : moving.InitialController;
}
