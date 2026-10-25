using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Fail-fast doubles for isolated legacy tests. Never part of the application.
/// </summary>
internal static class Stubs
{
    private static NotImplementedException Missing(string subsystem, string agent) =>
        new($"{subsystem} is not built yet: wave 1, agent {agent}.");

    public sealed class Turn : ITurnSystem
    {
        public string Name => "turn and priority";

        public Outcome Begin(GameState state) => throw Missing(Name, "E2");

        public Outcome Pass(GameState state, PlayerId player) => throw Missing(Name, "E2");

        public Outcome Advance(GameState state) => throw Missing(Name, "E2");
    }

    /// <summary>Likewise: with no trigger matching, nothing triggers.</summary>
    public sealed class Triggers : ITriggerSystem
    {
        public string Name => "triggers";

        public Outcome Collect(GameState state, IReadOnlyList<GameEvent> since) => Outcome.Unchanged(state);
    }

    public sealed class Replacement : IReplacementSystem
    {
        public string Name => "replacement and prevention";

        public Outcome Replace(GameState state, GameEvent proposed, PlayerId affected) => throw Missing(Name, "E8");
    }

    public sealed class Interpreter : IInterpreter
    {
        public string Name => "interpreter";

        public Outcome Resolve(GameState state, ObjectId stackObject) => throw Missing(Name, "E9");

        public Outcome Run(GameState state, Effect effect, EffectContext context) => throw Missing(Name, "E9");
    }

    public sealed class Choices : IChoiceSystem
    {
        public string Name => "choices";

        public Outcome Answer(GameState state, AnswerChoice command) => throw Missing(Name, "E13");
    }

}
