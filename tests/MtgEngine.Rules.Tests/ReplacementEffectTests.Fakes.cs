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

    public Func<GameState, Effect, EffectContext, Outcome>? Instead { get; init; }

    public Outcome Resolve(GameState state, ObjectId stackObject) => throw new NotSupportedException();

    public Outcome Run(GameState state, Effect effect, EffectContext context)
    {
        Ran.Add(effect);
        Contexts.Add(context);
        return Instead?.Invoke(state, effect, context) ?? Outcome.Unchanged(state);
    }
}
