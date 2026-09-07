using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The interpreter is E9's; a replacement only needs to know that it handed the effect over and
/// that the original event did not happen. This one records what it was asked to run and, when
/// a test gives it one, performs a stand-in for it.
/// </summary>
internal sealed class ReplacementEffectFakeInterpreter : IInterpreter
{
    public string Name => "fake interpreter";

    public List<Effect> Ran { get; } = [];

    public List<EffectContext> Contexts { get; } = [];

    /// <summary>
    /// Which ability of the source each run was told it came from, CR 101.4: an answer finds the
    /// effect that asked by that index, so a run handed none is a question nobody can answer.
    /// </summary>
    public List<int?> Indexes { get; } = [];

    public Func<GameState, Effect, EffectContext, Outcome>? Instead { get; init; }

    public Outcome Resolve(GameState state, ObjectId stackObject) => throw new NotSupportedException();

    public Outcome Run(GameState state, Effect effect, EffectContext context)
    {
        Ran.Add(effect);
        Contexts.Add(context);
        return Instead?.Invoke(state, effect, context) ?? Outcome.Unchanged(state);
    }

    public Outcome Run(GameState state, Effect effect, EffectContext context, int? abilityIndex)
    {
        Indexes.Add(abilityIndex);
        return Run(state, effect, context);
    }
}
