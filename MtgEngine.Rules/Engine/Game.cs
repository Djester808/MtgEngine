using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Engine;

/// <summary>The subsystems composed. Wave-1 builders replace the stubs one seam at a time; only the coordinator edits the wiring.</summary>
public sealed record Subsystems(
    IQuery Query,
    IZoneSystem Zones,
    ITurnSystem Turn,
    ICastingSystem Casting,
    ICombatSystem Combat,
    IStateBasedActions Sba,
    ITriggerSystem Triggers,
    ICharacteristics Characteristics,
    IReplacementSystem Replacement,
    IInterpreter Interpreter,
    IChoiceSystem Choices,
    IViewProjector Views,
    ILegalActions Legal)
{
    /// <summary>Every seam stubbed: the engine as it stands before wave 1.</summary>
    public static Subsystems AllStubs() => new(
        new Stubs.Query(), new Stubs.Zones(), new Stubs.Turn(), new Stubs.Casting(), new Stubs.Combat(), new Stubs.Sba(),
        new Stubs.Triggers(), new Stubs.Layers(), new Stubs.Replacement(), new Stubs.Interpreter(),
        new Stubs.Choices(), new Stubs.Views(), new Stubs.Legal());

    /// <summary>
    /// What wave 1 built, composed: layers, query, zones, casting, turns, combat and state-based
    /// actions are real; triggers, replacement, the interpreter, choices and views are still
    /// stubs, which is enough for a game of lands and vanilla creatures.
    /// </summary>
    public static Subsystems Wave1(Func<string, bool>? enforced = null)
    {
        var layers = new Layers.LayerSystem();
        var query = new Query.Query(layers);
        var zones = new Zones.ZoneSystem();
        var interpreter = new Stubs.Interpreter();
        var casting = new Casting.CastingSystem(zones, query, layers, interpreter);
        // CR 510.1 and 703.4m: combat damage is the turn system's action, so it needs the combat system.
        var combat = new Combat.CombatSystem(layers, query);
        var turn = new Turn.TurnSystem(zones, casting, combat, layers);
        var sba = new Sba.StateBasedActions(zones, layers, query);
        return new Subsystems(
            query, zones, turn, casting, combat, sba, new Stubs.Triggers(), layers, new Stubs.Replacement(), interpreter, new Stubs.Choices(),
            new MtgEngine.Rules.Views.ViewProjector(layers, enforced ?? (_ => true)),
            new MtgEngine.Rules.Legal.LegalActions(casting, layers));
    }

    /// <summary>
    /// What wave 2 built on top of wave 1: triggers, replacement, the interpreter, the choice
    /// system, views and legal actions are all real. Cards with abilities play through this one.
    /// <paramref name="enforced"/> says, by oracle id, whether the engine plays a card or the table
    /// does by hand; the session that admitted the deck knows, the engine does not.
    /// </summary>
    public static Subsystems Wave2(Func<string, bool>? enforced = null)
    {
        var layers = new Layers.LayerSystem();
        var query = new Query.Query(layers);
        var zones = new Zones.ZoneSystem();

        // The replacement system runs an "instead" effect through the interpreter, and the
        // interpreter offers its damage through the replacement system: a cycle, broken by a
        // handle the interpreter is set into once both exist.
        var handle = new ForwardingInterpreter();
        var replacement = new MtgEngine.Rules.Replacement.ReplacementSystem(query, layers, handle);
        var interpreter = new MtgEngine.Rules.Interpreter.EffectInterpreter(query, zones, replacement, layers);
        handle.Target = interpreter;

        var casting = new Casting.CastingSystem(zones, query, layers, interpreter);
        var combat = new Combat.CombatSystem(layers, query);
        var turn = new Turn.TurnSystem(zones, casting, combat, layers);
        var sba = new Sba.StateBasedActions(zones, layers, query);
        var triggers = new MtgEngine.Rules.Triggers.TriggerSystem(query, layers);
        // The interpreter implements IResumable, so a card's question resumes through it (CR 101.4).
        var choices = new MtgEngine.Rules.Choices.ChoiceSystem(zones, turn, interpreter);
        return new Subsystems(
            query, zones, turn, casting, combat, sba, triggers, layers, replacement, interpreter, choices,
            new MtgEngine.Rules.Views.ViewProjector(layers, enforced ?? (_ => true)),
            new MtgEngine.Rules.Legal.LegalActions(casting, layers));
    }
}

/// <summary>A command was applied: the events, and the state after them.</summary>
public abstract record GameResult;

public sealed record Applied(GameState State, ImmutableList<GameEvent> Events) : GameResult;

public sealed record Refused(string Reason, string? Rule = null) : GameResult;

/// <summary>Thrown inside a subsystem when the rules forbid the command. <see cref="Game.Apply"/> turns it into a <see cref="Refused"/>.</summary>
public sealed class RefusedException(string reason, string? rule = null) : Exception(reason)
{
    public string Reason { get; } = reason;

    public string? Rule { get; } = rule;
}

/// <summary>
/// The engine's front door: create a game, apply a command, get events back. This is the
/// only entry point the hub, the scenario runner and the tests use. It owns the settle loop:
/// after any command, state-based actions and triggers repeat until nothing more happens,
/// CR 117.5, 704.3 and 603.3.
/// </summary>
public sealed class Game(Subsystems subsystems, IDefinitionStore store)
{
    public Subsystems Engine { get; } = subsystems;

    public IDefinitionStore Store { get; } = store;

    /// <summary>
    /// CR 103: the zones are set up, mulligans are offered when the setup asks for them (the turn
    /// system that implements <see cref="ITurnSystem.OpenHands"/> begins turn 1 after the last
    /// keep), otherwise turn 1 begins here.
    /// </summary>
    [RuleRef("103")]
    public Outcome Create(GameSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        return Engine.Zones.Create(setup, Store)
            .Then(s => setup.OfferMulligans ? Engine.Turn.OpenHands(s) : Outcome.Unchanged(s))
            .Then(s => s.Pending is null ? Engine.Turn.Begin(s) : Outcome.Unchanged(s))
            .Then(s => Settle(s));
    }

    public GameResult Apply(GameState state, Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (state.Outcome is not null)
            return new Refused("The game is over.", "104.1");
        if (state.Pending is not null && command is not AnswerChoice and not Concede)
            return new Refused($"{state.Pending.Player} has a choice to make first: {state.Pending.Prompt}", "101.4");
        if (state.Pending is not null && command is AnswerChoice answer && answer.Player != state.Pending.Player)
            return new Refused("That choice is not yours to make.", "101.4");

        try
        {
            var outcome = command switch
            {
                CastSpell c => Engine.Casting.Cast(state, c),
                ActivateAbility a => Engine.Casting.Activate(state, a),
                PlayLand p => Engine.Casting.PlayLand(state, p),
                DeclareAttackers d => Engine.Combat.DeclareAttackers(state, d),
                DeclareBlockers d => Engine.Combat.DeclareBlockers(state, d),
                PassPriority p => Engine.Turn.Pass(state, p.Player),
                AnswerChoice a => Engine.Choices.Answer(state, a),
                Concede c => ConcedeGame(state, c.Player),
                ManualMove m => Manual(state, m),
                ManualTap m => Manual(state, m),
                ManualCounters m => Manual(state, m),
                ManualLife m => Manual(state, m),
                _ => throw new RefusedException($"Unknown command {command.GetType().Name}."),
            };
            var settled = Settle(outcome.State, outcome.Events);
            return new Applied(settled.State, outcome.Events.AddRange(settled.Events));
        }
        catch (RefusedException refused)
        {
            return new Refused(refused.Reason, refused.Rule);
        }
    }

    /// <summary>
    /// What happens before anyone gets priority, CR 117.5 and 704.3: the triggers of what the
    /// command itself did (<paramref name="since"/>, CR 603.3), then state-based actions and the
    /// triggers of those, until a pass changes nothing. Stops at a pending choice.
    /// </summary>
    [RuleRef("704.3")]
    public Outcome Settle(GameState state, IReadOnlyList<GameEvent>? since = null)
    {
        var all = ImmutableList<GameEvent>.Empty;
        if (since is { Count: > 0 } && state.Pending is null && state.Outcome is null)
        {
            var fired = Engine.Triggers.Collect(state, since);
            state = fired.State;
            all = all.AddRange(fired.Events);
        }

        for (var round = 0; round < 1000; round++)
        {
            if (state.Pending is not null || state.Outcome is not null)
                break;
            var sba = Engine.Sba.Check(state);
            var triggers = Engine.Triggers.Collect(sba.State, sba.Events);
            var produced = sba.Events.AddRange(triggers.Events);
            state = triggers.State;
            all = all.AddRange(produced);
            if (produced.Count == 0)
                break;
        }

        return new Outcome(state, all);
    }

    [RuleRef("104.3a")]
    private static Outcome ConcedeGame(GameState state, PlayerId player)
    {
        var events = ImmutableList.Create<GameEvent>(new PlayerLost(player, "conceded"));
        var next = GameReducer.Apply(state, events);
        var remaining = next.Players.Where(p => !p.HasLost).Select(p => p.Id).ToImmutableList();
        if (remaining.Count <= 1)
        {
            var ended = new GameEnded(new GameOutcome(remaining, remaining.Count == 0, "concession"));
            events = events.Add(ended);
            next = ended.Apply(next);
        }

        return new Outcome(next, events);
    }

    /// <summary>Honour-system actions. The table decides which objects may be handled this way; here they are only logged and applied.</summary>
    private static Outcome Manual(GameState state, Command command)
    {
        ImmutableList<GameEvent> events = command switch
        {
            ManualMove m when state.Objects.TryGetValue(m.Object, out var obj) =>
            [
                new ManualActionTaken(m.Player, "move", m.Object, $"{obj.Zone.Zone} to {m.To.Zone}"),
                new ObjectMoved(m.Object, obj with { Id = new ObjectId(state.NextObjectId), Zone = m.To, Tapped = m.Tapped, Timestamp = state.NextTimestamp, Damage = 0, Counters = ImmutableDictionary<string, int>.Empty, AttachedTo = null }, m.Position),
            ],
            ManualTap t when state.Objects.ContainsKey(t.Object) =>
            [
                new ManualActionTaken(t.Player, t.Tapped ? "tap" : "untap", t.Object, string.Empty),
                new ObjectChanged(t.Object, Tapped: t.Tapped),
            ],
            ManualCounters c when state.Objects.ContainsKey(c.Object) =>
            [
                new ManualActionTaken(c.Player, "counters", c.Object, $"{c.Kind} {c.Delta:+#;-#;0}"),
                new CountersChanged(c.Object, c.Kind, c.Delta),
            ],
            ManualLife l =>
            [
                new ManualActionTaken(l.Player, "life", null, $"{l.Target} {l.Delta:+#;-#;0}"),
                new LifeChanged(l.Target, l.Delta, "manual"),
            ],
            _ => throw new RefusedException("No such object."),
        };
        return new Outcome(GameReducer.Apply(state, events), events);
    }
}
