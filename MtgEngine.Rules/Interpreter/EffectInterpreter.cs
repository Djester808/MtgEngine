using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// Executes effect trees, CR 608.2. Resolution builds the context from what was chosen when the
/// spell was cast, re-checks the targets (CR 608.2b) and walks the tree; every state change goes
/// through an event, and a question the game has to ask stops the walk with a
/// <see cref="PendingChoice"/> rather than a continuation.
/// </summary>
[RuleRef("608.2")]
public sealed partial class EffectInterpreter(
    IQuery query,
    IZoneSystem zones,
    IReplacementSystem replacements,
    ICharacteristics characteristics) : IInterpreter
{
    private static readonly ImmutableList<ChoiceOption> YesNo =
        [new ChoiceOption("yes", "Yes"), new ChoiceOption("no", "No")];

    public string Name => "interpreter";

    /// <summary>
    /// Resolve the spell or ability on the stack, CR 608.2. The object stays where it is: moving
    /// it to its owner's graveyard, or off the stack, is the casting system's last step.
    /// </summary>
    [RuleRef("608.2")]
    public Outcome Resolve(GameState state, ObjectId stackObject)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Objects.TryGetValue(stackObject, out var obj))
            return Outcome.Unchanged(state);

        var entry = obj.Stack
            ?? throw new InvalidOperationException($"{stackObject} has no stack entry and cannot resolve (CR 608.2)");
        var controller = characteristics.ControllerOf(state, stackObject);
        var effect = EffectOf(obj, entry);
        var context = ContextFor(state, obj, entry, controller);

        // CR 608.2b: an illegal target is skipped, and a spell all of whose targets are illegal
        // does not resolve at all.
        var legal = LegalTargets(state, entry, effect, context);
        if (entry.Targets.Count > 0 && legal.Count == 0 && entry.TargetPlayers.Count == 0)
            return new Outcome(state, [new StackObjectResolved(stackObject)]);

        context = context with { Targets = legal };
        if (effect is null)
            return new Outcome(state, [new StackObjectResolved(stackObject)]);

        var run = Execute(new EffectRun(state, context, [], false, "0", entry.AbilityIndex), effect);
        return run.Halted
            ? new Outcome(run.State, run.Events)
            : new Outcome(run.State, run.Events.Add(new StackObjectResolved(stackObject)));
    }

    /// <summary>Run an effect outside the stack: a replacement's With, a scenario step, a delayed trigger.</summary>
    public Outcome Run(GameState state, Effect effect, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(context);
        var run = Execute(new EffectRun(state, context, [], false, "0"), effect);
        return new Outcome(run.State, run.Events);
    }

    /// <summary>What the resolving object does: a spell's spell abilities, or the one ability that was put on the stack.</summary>
    private static Effect? EffectOf(GameObject obj, StackEntry entry)
    {
        var abilities = obj.Face.AbilityList;
        if (entry.AbilityIndex is { } index)
            return index >= 0 && index < abilities.Count ? Body(abilities[index]) : null;

        // CR 608.2c: a spell does what its spell abilities say, in printed order.
        var spells = abilities.OfType<SpellAbility>().Select(a => a.Do).ToList();
        return spells.Count switch { 0 => null, 1 => spells[0], _ => new Sequence(spells) };
    }

    private static Effect? Body(Ability ability) => ability switch
    {
        SpellAbility spell => spell.Do,
        TriggeredAbility triggered => triggered.Do,
        ActivatedAbility activated => activated.Do,
        _ => null,
    };

    /// <summary>
    /// The context a resolution runs in: what was chosen on casting, plus the triggering objects
    /// under the reserved key "triggering" so a card can say "that creature" either way.
    /// </summary>
    private static EffectContext ContextFor(GameState state, GameObject obj, StackEntry entry, PlayerId controller)
    {
        var triggering = TriggeringOf(state, entry);
        var chosen = ImmutableDictionary<string, string>.Empty;
        if (entry.Modes.Count > 0)
            chosen = chosen.SetItem("modes", string.Join(",", entry.Modes.Select(m => m.ToString(CultureInfo.InvariantCulture))));

        // CR 603.3d: what a triggered ability triggered on travels with its stack object, under
        // the trigger system's key and as the context's Triggering; anything else the stack
        // object remembers rides along.
        var remembered = (entry.Remembered ?? ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty).SetItem("triggering", triggering);
        var triggeringPlayer = entry.RememberedPlayers is { } players && players.TryGetValue("triggering", out var those) && those.Count > 0
            ? those[0]
            : (PlayerId?)null;

        return new EffectContext(
            entry.Source ?? obj.Id,
            controller,
            entry.Targets,
            entry.TargetPlayers,
            entry.X,
            remembered,
            chosen,
            ImmutableDictionary<string, int>.Empty,
            triggering,
            triggeringPlayer,
            entry.RememberedPlayers);
    }

    /// <summary>
    /// What the trigger was about: what the stack object carries (CR 603.3d), or, for an ability
    /// stacked without it, its own source, which is right for the "when this enters" shape.
    /// </summary>
    private static ImmutableList<ObjectId> TriggeringOf(GameState state, StackEntry entry)
    {
        if (entry.Triggering is { } carried)
            return carried;
        return entry.AbilityIndex is not null && entry.Source is { } source && state.Objects.ContainsKey(source)
            ? [source]
            : [];
    }

    /// <summary>
    /// The targets that are still legal, CR 608.2b. Each chosen target is re-checked against the
    /// filter of the <see cref="Target"/> selector it came from, in the order the tree names them.
    /// </summary>
    [RuleRef("608.2b")]
    private ImmutableList<ObjectId> LegalTargets(GameState state, StackEntry entry, Effect? effect, EffectContext context)
    {
        if (entry.Targets.Count == 0)
            return entry.Targets;

        var filters = effect is null ? [] : EffectWalk.TargetsIn(effect);
        var legal = ImmutableList.CreateBuilder<ObjectId>();
        for (var i = 0; i < entry.Targets.Count; i++)
        {
            var id = entry.Targets[i];
            if (!state.Objects.ContainsKey(id))
                continue;
            var filter = filters.Count == 0 ? null : filters[Math.Min(i, filters.Count - 1)].Filter;
            if (filter is null || query.Matches(state, filter, id, context))
                legal.Add(id);
        }

        return legal.ToImmutable();
    }

    /* ---------------------------------------------------------------------------------------- *
     * The walk itself.
     * ---------------------------------------------------------------------------------------- */

    /// <summary>
    /// One step of a resolution: where the state is, what the effect remembers, and whether the
    /// game stopped to ask. The last four fields are only set while a question is being resumed:
    /// which ability is resolving, the path back to the node that asked, and the answer it is
    /// waiting for. There is never a captured continuation, so a game rebuilt from its log
    /// resumes the same way (CR 101.4).
    /// </summary>
    internal sealed record EffectRun(
        GameState State,
        EffectContext Context,
        ImmutableList<GameEvent> Events,
        bool Halted,
        string Path,
        int? AbilityIndex = null,
        string? Resume = null,
        Resumption? Answer = null,
        string? AnswerPath = null);

    private EffectRun Execute(EffectRun run, Effect effect)
    {
        if (run.Halted)
            return run;

        // The node that asked runs again with the answer bound; everything under it is work that
        // has not happened yet, so the walk stops skipping here.
        if (run.Resume is { } target && string.Equals(target, run.Path, StringComparison.Ordinal))
            run = run with { Resume = null };

        return Structure(run, effect) ?? Verb(run, effect) ?? Action(run, effect) ?? FaceVerb(run, effect)
            ?? throw new NotImplementedException($"no interpreter arm for {effect.GetType().Name}");
    }

    /// <summary>
    /// Run one sub-effect. While a resumption walks back to the question it came from, the steps
    /// that already happened are skipped and only the branch holding the question is entered;
    /// everything after that branch runs normally, because the answer put the walk back where it
    /// stopped (CR 101.4).
    /// </summary>
    [RuleRef("101.4")]
    private EffectRun Child(EffectRun run, int index, Effect effect)
    {
        var descended = Descend(run, index);
        if (run.Resume is not { } target)
            return Execute(descended, effect) with { Path = run.Path };
        if (!OnPath(descended.Path, target))
            return run;
        return Execute(descended, effect) with { Path = run.Path, Resume = null };
    }

    /// <summary>Is the question's path this node, or somewhere inside it?</summary>
    private static bool OnPath(string path, string target) =>
        string.Equals(path, target, StringComparison.Ordinal)
        || target.StartsWith(path + ".", StringComparison.Ordinal);

    /// <summary>
    /// The answer to the question this very node asked, or null when nothing is being resumed
    /// here. An arm that asks checks this first, so resuming it performs the instruction instead
    /// of asking the same question twice.
    /// </summary>
    private static ImmutableList<ChoiceOption>? Answered(EffectRun run) =>
        run.Answer is { } answer && string.Equals(run.AnswerPath, run.Path, StringComparison.Ordinal)
            ? answer.Chosen
            : null;

    /// <summary>The objects an answer named that are still in the game.</summary>
    private static ImmutableList<ObjectId> Named(GameState state, IEnumerable<ChoiceOption> options) =>
        [.. options.Select(o => o.Object).OfType<ObjectId>().Where(state.Objects.ContainsKey)];

    /* ---------------------------------------------------------------------------------------- *
     * Shared plumbing: events, sub-runs, selectors, players, numbers, questions.
     * ---------------------------------------------------------------------------------------- */

    private static EffectRun Emit(EffectRun run, params GameEvent[] events)
    {
        var state = run.State;
        foreach (var e in events)
            state = e.Apply(state);
        return run with { State = state, Events = run.Events.AddRange(events) };
    }

    private static EffectRun Merge(EffectRun run, Outcome outcome) =>
        run with { State = outcome.State, Events = run.Events.AddRange(outcome.Events) };

    private EffectRun Propose(EffectRun run, GameEvent proposed, PlayerId affected) =>
        Merge(run, replacements.Replace(run.State, proposed, affected));

    private EffectRun Ask(
        EffectRun run,
        PlayerId player,
        string kind,
        string prompt,
        ImmutableList<ChoiceOption> options,
        int min,
        int max,
        string? storeAs = null)
    {
        var extra = ImmutableDictionary<string, string>.Empty;
        if (storeAs is not null)
            extra = extra.SetItem("storeAs", storeAs);
        var choice = new PendingChoice(
            new ChoiceId(run.State.NextChoiceId),
            player,
            kind,
            prompt,
            options,
            min,
            max,
            new ChoiceContext(run.Context.Source, run.AbilityIndex, run.Path, extra));
        return Emit(run, new ChoiceRequested(choice)) with { Halted = true };
    }

    private ImmutableList<ObjectId> Objects(EffectRun run, Selector? selector) =>
        selector is null ? [] : query.Resolve(run.State, selector, run.Context);

    /// <summary>A player reference that may be omitted, which always means the controller, CR 109.5.</summary>
    private ImmutableList<PlayerId> PlayersOrYou(EffectRun run, PlayerRef? who) =>
        who is null ? [run.Context.Controller] : query.Players(run.State, who, run.Context);

    private ImmutableList<PlayerId> Players(EffectRun run, PlayerRef? who) =>
        who is null ? [] : query.Players(run.State, who, run.Context);

    private PlayerId FirstPlayer(EffectRun run, PlayerRef? who)
    {
        var players = PlayersOrYou(run, who);
        return players.Count > 0 ? players[0] : run.Context.Controller;
    }

    private int Number(EffectRun run, Amount? amount, int whenMissing = 1) =>
        amount is null ? whenMissing : query.Evaluate(run.State, amount, run.Context);

    private static EffectRun Recall(EffectRun run, string? key, IEnumerable<ObjectId> objects) =>
        key is null ? run : run with { Context = run.Context with { Remembered = run.Context.Remembered.SetItem(key, [.. objects]) } };

    private static ImmutableList<ChoiceOption> OptionsFor(GameState state, IEnumerable<ObjectId> ids) =>
        [.. ids.Where(state.Objects.ContainsKey).Select(id => new ChoiceOption(
            id.Value.ToString(CultureInfo.InvariantCulture),
            state.Objects[id].Face.Name,
            id))];
}
