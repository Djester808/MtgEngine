using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Engine;

/// <summary>
/// The interpreter, for a subsystem built before it. The replacement system runs an "instead"
/// effect through the interpreter (CR 614.1a) and the interpreter offers its damage through the
/// replacement system (CR 614.6), so one of the two is handed this and the target is set once
/// both exist. Every call forwards; a call before the target is set is a composition bug.
/// </summary>
public sealed class ForwardingInterpreter : IInterpreter
{
    public IInterpreter? Target { get; set; }

    public string Name => Target?.Name ?? "interpreter (not yet set)";

    private IInterpreter Bound => Target ?? throw new InvalidOperationException("The interpreter handle was used before its target was set.");

    public Outcome Resolve(GameState state, ObjectId stackObject) => Bound.Resolve(state, stackObject);

    public Outcome Run(GameState state, Effect effect, EffectContext context) => Bound.Run(state, effect, context);

    public Outcome Run(GameState state, Effect effect, EffectContext context, int? abilityIndex) => Bound.Run(state, effect, context, abilityIndex);
}
