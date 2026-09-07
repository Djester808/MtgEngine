using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Engine;

/// <summary>
/// The seams between subsystems. Each wave-1 builder implements one of these in its own
/// folder; <see cref="Engine"/> composes them; nothing else crosses a folder boundary. A
/// builder who needs a seam changed writes a seam request rather than editing this file.
/// </summary>
public interface ISubsystem
{
    string Name { get; }
}

/// <summary>What a subsystem hands back: the new state and the events that made it.</summary>
public sealed record Outcome(GameState State, ImmutableList<GameEvent> Events)
{
    public static Outcome Unchanged(GameState state) => new(state, []);

    public Outcome Then(Func<GameState, Outcome> next)
    {
        var following = next(State);
        return new Outcome(following.State, Events.AddRange(following.Events));
    }
}

/// <summary>A command the rules do not allow, and which rule says so.</summary>
public sealed record Refusal(string Reason, string? Rule = null);

/// <summary>Zones and objects, CR 400: creating the game, moving objects, drawing, shuffling, tokens.</summary>
[RuleRef("400")]
public interface IZoneSystem : ISubsystem
{
    Outcome Create(GameSetup setup, IDefinitionStore store);

    Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where = Placement.Default, bool tapped = false, PlayerId? controller = null);

    Outcome Draw(GameState state, PlayerId player, int count);

    Outcome Shuffle(GameState state, PlayerId player);

    Outcome CreateToken(GameState state, TokenSpec token, PlayerId controller, bool tapped, bool attacking);
}

/// <summary>Turn structure and priority, CR 500 and 117: steps, untap, draw, cleanup, passing, N players.</summary>
[RuleRef("500")]
public interface ITurnSystem : ISubsystem
{
    /// <summary>Start the first turn after creation: mulligans are the choice system's; this begins turn 1.</summary>
    Outcome Begin(GameState state);

    /// <summary>A player passes priority. When all pass in succession, the stack resolves or the step advances.</summary>
    Outcome Pass(GameState state, PlayerId player);

    /// <summary>Advance to the next step or turn, performing turn-based actions, CR 703.</summary>
    Outcome Advance(GameState state);
}

/// <summary>Mana, costs, casting, activating and the stack, CR 601–608 and 106.</summary>
[RuleRef("601")]
public interface ICastingSystem : ISubsystem
{
    Outcome Cast(GameState state, CastSpell command);

    Outcome Activate(GameState state, ActivateAbility command);

    Outcome PlayLand(GameState state, PlayLand command);

    /// <summary>Resolve the top object of the stack, CR 608.</summary>
    Outcome ResolveTop(GameState state);

    /// <summary>Whether the command would be legal now, with the rule when it would not.</summary>
    Refusal? Check(GameState state, Command command);
}

[RuleRef("506")]
public interface ICombatSystem : ISubsystem
{
    Outcome DeclareAttackers(GameState state, DeclareAttackers command);

    Outcome DeclareBlockers(GameState state, DeclareBlockers command);

    Outcome DealCombatDamage(GameState state, bool firstStrike);

    Outcome EndCombat(GameState state);
}

[RuleRef("704")]
public interface IStateBasedActions : ISubsystem
{
    /// <summary>Perform all state-based actions that apply, repeatedly, until none do, CR 704.3.</summary>
    Outcome Check(GameState state);
}

[RuleRef("603")]
public interface ITriggerSystem : ISubsystem
{
    /// <summary>Find abilities that triggered on these events and put them on the stack in APNAP order, CR 603.3.</summary>
    Outcome Collect(GameState state, IReadOnlyList<GameEvent> since);
}

/// <summary>The layer system, CR 613. Characteristics are computed, never stored; controller included.</summary>
[RuleRef("613")]
public interface ICharacteristics : ISubsystem
{
    Characteristics Of(GameState state, ObjectId id);

    PlayerId ControllerOf(GameState state, ObjectId id);
}

/// <summary>What an object is right now, after every layer.</summary>
public sealed record Characteristics(
    string Name,
    CardType Types,
    Supertype Supertypes,
    ImmutableList<string> Subtypes,
    Color Colors,
    int? Power,
    int? Toughness,
    int? Loyalty,
    int? Defense,
    string? ManaCost,
    ImmutableList<Ability> Abilities,
    PlayerId Controller)
{
    public bool Has(KeywordKind keyword) => Abilities.OfType<KeywordAbility>().Any(k => k.Keyword == keyword);
}

[RuleRef("614")]
public interface IReplacementSystem : ISubsystem
{
    /// <summary>Offer an event to every applicable replacement and prevention effect; return what actually happens, CR 616.</summary>
    Outcome Replace(GameState state, GameEvent proposed, PlayerId affected);
}

/// <summary>Executes effect trees. Owns selector resolution, remembered objects and the keyword actions.</summary>
public interface IInterpreter : ISubsystem
{
    Outcome Resolve(GameState state, ObjectId stackObject);

    /// <summary>Run an effect outside the stack: a replacement's With, a static ability's setup, a manual scenario step.</summary>
    Outcome Run(GameState state, Effect effect, EffectContext context);
}

/// <summary>What an effect runs inside: its source, controller, chosen targets and the memory earlier steps left.</summary>
public sealed record EffectContext(
    ObjectId? Source,
    PlayerId Controller,
    ImmutableList<ObjectId> Targets,
    ImmutableList<PlayerId> TargetPlayers,
    int? X,
    ImmutableDictionary<string, ImmutableList<ObjectId>> Remembered,
    ImmutableDictionary<string, string> Chosen,
    ImmutableDictionary<string, int> Stored,
    ImmutableList<ObjectId> Triggering,
    PlayerId? TriggeringPlayer);

/// <summary>Pending questions and their answers, resumed by kind, never by continuation.</summary>
public interface IChoiceSystem : ISubsystem
{
    Outcome Answer(GameState state, AnswerChoice command);
}

/// <summary>Per-player projection: what a viewer may see, and what they may do.</summary>
public interface IViewProjector : ISubsystem
{
    Views.GameView Project(GameState state, PlayerId viewer);
}

public interface ILegalActions : ISubsystem
{
    Views.LegalActions For(GameState state, PlayerId player);
}

/// <summary>How a game is created.</summary>
public sealed record GameSetup(ImmutableList<PlayerStart> Players, ulong Seed, int StartingLife = 20, string Format = "commander", int OpeningHandSize = 7);
