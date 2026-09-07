using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Engine;

/// <summary>
/// Every seam before its builder lands. Each stub throws naming the subsystem and the wave-1
/// agent that owns it, so a game that reaches an unbuilt seam says which one.
/// </summary>
public static class Stubs
{
    private static NotImplementedException Missing(string subsystem, string agent) =>
        new($"{subsystem} is not built yet: wave 1, agent {agent}.");

    public sealed class Query : IQuery
    {
        public string Name => "query layer";

        public bool Matches(GameState state, ObjectFilter filter, ObjectId candidate, EffectContext context) => throw Missing(Name, "E0");

        public bool Holds(GameState state, Condition condition, EffectContext context) => throw Missing(Name, "E0");

        public int Evaluate(GameState state, Amount amount, EffectContext context) => throw Missing(Name, "E0");

        public ImmutableList<ObjectId> Resolve(GameState state, Selector selector, EffectContext context) => throw Missing(Name, "E0");

        public ImmutableList<PlayerId> Players(GameState state, PlayerRef player, EffectContext context) => throw Missing(Name, "E0");
    }

    public sealed class Zones : IZoneSystem
    {
        public string Name => "zones";

        public Outcome Create(GameSetup setup, IDefinitionStore store) => throw Missing(Name, "E1");

        public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where = Placement.Default, bool tapped = false, PlayerId? controller = null) => throw Missing(Name, "E1");

        public Outcome Draw(GameState state, PlayerId player, int count) => throw Missing(Name, "E1");

        public Outcome Shuffle(GameState state, PlayerId player) => throw Missing(Name, "E1");

        public Outcome CreateToken(GameState state, TokenSpec token, PlayerId controller, bool tapped, bool attacking) => throw Missing(Name, "E1");
    }

    public sealed class Turn : ITurnSystem
    {
        public string Name => "turn and priority";

        public Outcome Begin(GameState state) => throw Missing(Name, "E2");

        public Outcome Pass(GameState state, PlayerId player) => throw Missing(Name, "E2");

        public Outcome Advance(GameState state) => throw Missing(Name, "E2");
    }

    public sealed class Casting : ICastingSystem
    {
        public string Name => "mana, costs, casting and the stack";

        public Outcome Cast(GameState state, CastSpell command) => throw Missing(Name, "E3");

        public Outcome Activate(GameState state, ActivateAbility command) => throw Missing(Name, "E3");

        public Outcome PlayLand(GameState state, PlayLand command) => throw Missing(Name, "E3");

        public Outcome ResolveTop(GameState state) => throw Missing(Name, "E3");

        public Refusal? Check(GameState state, Command command) => throw Missing(Name, "E3");
    }

    public sealed class Combat : ICombatSystem
    {
        public string Name => "combat";

        public Outcome DeclareAttackers(GameState state, DeclareAttackers command) => throw Missing(Name, "E4");

        public Outcome DeclareBlockers(GameState state, DeclareBlockers command) => throw Missing(Name, "E4");

        public Outcome DealCombatDamage(GameState state, bool firstStrike) => throw Missing(Name, "E4");

        public Outcome EndCombat(GameState state) => throw Missing(Name, "E4");
    }

    /// <summary>The one stub that does not throw: with no rules, nothing is ever wrong with the state.</summary>
    public sealed class Sba : IStateBasedActions
    {
        public string Name => "state-based actions";

        public Outcome Check(GameState state) => Outcome.Unchanged(state);
    }

    /// <summary>Likewise: with no trigger matching, nothing triggers.</summary>
    public sealed class Triggers : ITriggerSystem
    {
        public string Name => "triggers";

        public Outcome Collect(GameState state, IReadOnlyList<GameEvent> since) => Outcome.Unchanged(state);
    }

    public sealed class Layers : ICharacteristics
    {
        public string Name => "continuous effects and layers";

        public Characteristics Of(GameState state, ObjectId id) => throw Missing(Name, "E7");

        public PlayerId ControllerOf(GameState state, ObjectId id) => throw Missing(Name, "E7");
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

    public sealed class Views : IViewProjector
    {
        public string Name => "view projection";

        public Rules.Views.GameView Project(GameState state, PlayerId viewer) => throw Missing(Name, "E14");
    }

    public sealed class Legal : ILegalActions
    {
        public string Name => "legal actions";

        public Rules.Views.LegalActions For(GameState state, PlayerId player) => throw Missing(Name, "E14");
    }
}
