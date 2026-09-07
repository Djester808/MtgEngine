using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// Resumption, CR 101.4. A question a card asked stopped the walk mid-resolution; the answer puts
/// the walk back exactly where it stopped. There is no continuation anywhere: the answer, the
/// stack object and the path recorded in the <see cref="ChoiceContext"/> are everything needed, so
/// a game rebuilt by replaying its log resumes the same way it did the first time.
/// </summary>
[RuleRef("101.4")]
public sealed partial class EffectInterpreter : IResumable
{
    /// <summary>
    /// Continue whatever the question interrupted. The kinds a rule asked about are finished by
    /// the subsystem that owns the rule; every other kind came from an effect, and is found again
    /// by its source, its ability index and its path inside that ability's effect tree.
    /// </summary>
    [RuleRef("101.4")]
    public Outcome Resume(GameState state, Resumption resumption)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(resumption);

        // CR 616.1: which applicable replacement applies first is the replacement system's loop,
        // and it is handed the answer unchanged rather than reinterpreted here.
        if (Is(resumption, ChoiceKinds.ReplacementOrder))
            return replacements.Resume(state, resumption);

        // CR 603.3b: the order this player's simultaneous triggers go on the stack.
        if (Is(resumption, ChoiceKinds.TriggerOrder))
            return Reordered(state, resumption);

        // CR 608.2: a question raised by a resolution is finished by resolving again. One raised
        // outside a resolution — a replacement's With, a delayed trigger — has no stack object, and
        // is found again in the abilities of the source the question carried.
        return Asking(state, resumption.Context) is { } asking
            ? Continue(state, asking, resumption)
            : Elsewhere(state, resumption);
    }

    /// <summary>
    /// The effect tree a question outside a resolution came from: ability
    /// <see cref="ChoiceContext.AbilityIndex"/> of its source — a <see cref="ReplacementAbility"/>'s
    /// <c>With</c> (CR 614.1a) or a triggered ability's body — or, when no index was given, the one
    /// delayed trigger that source created (CR 603.7). A question whose tree cannot be found is
    /// refused rather than thrown, so the game stays on the question instead of the engine's front
    /// door raising (CR 101.4).
    /// </summary>
    [RuleRef("101.4")]
    private Outcome Elsewhere(GameState state, Resumption resumption)
    {
        var context = resumption.Context;
        var effect = Tree(state, context)
            ?? throw new RefusedException(
                $"nothing is waiting on a \"{resumption.Kind}\" answer: the effect that asked cannot be found again",
                "101.4");

        var path = context.EffectPath;
        var run = Execute(
            new EffectRun(
                state,
                Bind(Outside(state, context), resumption),
                [],
                false,
                "0",
                context.AbilityIndex,
                path,
                resumption,
                path,
                OffStack: true),
            effect);
        return new Outcome(run.State, run.Events);
    }

    /// <summary>Which effect of the source asked, CR 614.1a and 603.7.</summary>
    private static Effect? Tree(GameState state, ChoiceContext context)
    {
        if (context.EffectPath is null || context.Source is not { } source)
            return null;

        if (state.Objects.TryGetValue(source, out var obj) && context.AbilityIndex is { } index)
        {
            var abilities = obj.Face.AbilityList;
            if (index >= 0 && index < abilities.Count)
                return abilities[index] is ReplacementAbility replacement ? replacement.With : Body(abilities[index]);
        }

        // CR 603.7: a delayed trigger's instruction is held in the game state, not on a card.
        var delayed = state.DelayedTriggers.Where(t => t.Source == source).ToList();
        return delayed.Count == 1 ? delayed[0].Do : null;
    }

    /// <summary>
    /// The context a question outside a resolution ran in, rebuilt from what the question carried:
    /// there is no stack entry to read it from, so the walk recorded its controller and what it
    /// was working on when it asked (CR 101.4).
    /// </summary>
    [RuleRef("101.4")]
    private EffectContext Outside(GameState state, ChoiceContext context)
    {
        var source = context.Source;
        var controller = context.Extra.TryGetValue("controller", out var seat)
            && int.TryParse(seat, CultureInfo.InvariantCulture, out var number)
                ? new PlayerId(number)
            : source is { } id && state.Objects.ContainsKey(id) ? characteristics.ControllerOf(state, id)
            : new PlayerId(0);

        var triggering = context.Extra.TryGetValue("triggering", out var those)
            ? Numbers(those).Select(n => new ObjectId(n)).ToImmutableList()
            : [];
        var triggeringPlayer = context.Extra.TryGetValue("triggeringPlayer", out var who)
            && int.TryParse(who, CultureInfo.InvariantCulture, out var seated)
                ? new PlayerId(seated)
                : (PlayerId?)null;

        return new EffectContext(
            source,
            controller,
            [],
            [],
            null,
            ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
            ImmutableDictionary<string, string>.Empty,
            ImmutableDictionary<string, int>.Empty,
            triggering,
            triggeringPlayer);
    }

    private static bool Is(Resumption resumption, string kind) =>
        string.Equals(resumption.Kind, kind, StringComparison.Ordinal);

    /// <summary>
    /// The stack object whose resolution asked: the one whose entry names the source the question
    /// carried, and the same ability of it. A question with no path did not come from an effect
    /// tree and is not this subsystem's to resume.
    /// </summary>
    private static ObjectId? Asking(GameState state, ChoiceContext context)
    {
        if (context.EffectPath is null)
            return null;
        foreach (var id in state.In(ZoneKey.Shared(Zone.Stack)))
        {
            if (!state.Objects.TryGetValue(id, out var obj) || obj.Stack is not { } entry)
                continue;
            if (context.Source is { } asked && (entry.Source ?? id) != asked && id != asked)
                continue;
            if (context.AbilityIndex is { } index && entry.AbilityIndex != index)
                continue;
            return id;
        }

        return null;
    }

    /// <summary>
    /// Resolve again, with the answer bound and the walk told which node to walk back to. The
    /// steps before that node are skipped rather than repeated, because they already happened and
    /// their events are already in the log (CR 608.2).
    /// </summary>
    [RuleRef("608.2")]
    private Outcome Continue(GameState state, ObjectId stackObject, Resumption resumption)
    {
        var obj = state.Objects[stackObject];
        var entry = obj.Stack
            ?? throw new InvalidOperationException($"{stackObject} has no stack entry and cannot resume (CR 608.2)");
        var effect = EffectOf(state, obj, entry);
        if (effect is null)
            return new Outcome(state, [new StackObjectResolved(stackObject)]);

        var context = ContextFor(state, obj, entry, characteristics.ControllerOf(state, stackObject));
        context = Bind(context with { Targets = LegalTargets(state, entry, effect, context) }, resumption);

        var path = resumption.Context.EffectPath;
        var run = Execute(
            new EffectRun(state, context, [], false, "0", entry.AbilityIndex, path, resumption, path),
            effect);
        return run.Halted
            ? new Outcome(run.State, run.Events)
            : new Outcome(run.State, run.Events.Add(new StackObjectResolved(stackObject)));
    }

    /// <summary>
    /// The answer, put where the effect that asked will read it. A question that named a key —
    /// "you may", "unless you pay", a mode, a chosen number — is answered by that key, which the
    /// arm looks up instead of asking again; the kinds that name objects are read at the node
    /// itself through <c>Answered</c>.
    /// </summary>
    private static EffectContext Bind(EffectContext context, Resumption resumption)
    {
        // CR 601.2c: targets chosen for this spell or ability, bound before the walk needs them.
        if (Is(resumption, ChoiceKinds.Targets))
            context = context with
            {
                Targets = [.. resumption.Chosen.Select(o => o.Object).OfType<ObjectId>()],
                TargetPlayers = [.. resumption.Chosen.Select(o => o.Player).OfType<PlayerId>()],
            };

        if (resumption.Context.Extra.TryGetValue("storeAs", out var key))
            context = context with
            {
                Chosen = context.Chosen.SetItem(key, string.Join(",", resumption.Chosen.Select(o => o.Id))),
            };

        return context;
    }

    /// <summary>
    /// CR 603.3b: a player whose abilities triggered at the same time puts them on the stack in
    /// any order they choose. The answer is that order, and it is one reorder of the stack: the
    /// objects that were asked about take the places they already occupy, in the order answered,
    /// and nothing else on the stack moves.
    /// </summary>
    [RuleRef("603.3b")]
    private static Outcome Reordered(GameState state, Resumption resumption)
    {
        var stack = ZoneKey.Shared(Zone.Stack);
        var chosen = Named(state, resumption.Chosen);
        var current = state.In(stack);
        var slots = Enumerable.Range(0, current.Count).Where(i => chosen.Contains(current[i])).ToList();
        if (chosen.Count == 0 || slots.Count != chosen.Count)
            return Outcome.Unchanged(state);

        var order = current.ToBuilder();
        for (var i = 0; i < slots.Count; i++)
            order[slots[i]] = chosen[i];

        var reordered = new ZoneReordered(stack, order.ToImmutable());
        return new Outcome(reordered.Apply(state), [reordered]);
    }
}
