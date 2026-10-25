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
    /// The prefix an answer already given rides under in a later question's context, CR 608.2. It
    /// is not a <see cref="ChoiceKinds"/> kind: it names a key inside one question's own extras,
    /// the way a loop's mark does, and never a kind of question anyone is asked.
    /// </summary>
    private const string AnsweredPrefix = "answered:";

    /// <summary>
    /// The prefix a remembered set rides under in a question's context, CR 608.2h: the objects an
    /// earlier step of the same walk named, which the resumed walk would otherwise have lost.
    /// </summary>
    internal const string RememberedPrefix = "remembered:";

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
        // and it is handed the answer unchanged rather than reinterpreted here. That is right for
        // a question the combat or zone layer raised, which has no effect tree waiting on it.
        // A question raised inside an effect walk belongs to the node that proposed the event:
        // the node hands the answer to the layer itself and then goes on with the instructions
        // after the proposal, which finishing the event here would drop for ever (CR 101.4).
        // The proposal ordinal is what separates the two — Propose is the only writer of it, and
        // the path does NOT separate them, because the layer's own question carries the literal
        // path "replacement" rather than no path at all.
        if (Is(resumption, ChoiceKinds.ReplacementOrder) && !resumption.Context.Extra.ContainsKey(ProposalKey))
            return Zones.ZoneMoveSnapshot.Resume(replacements, state, resumption);

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
                context.Source is null && effect is Search search
                    ? Library.StandaloneLibrarySearch.Save(search, Answers(resumption)) : Answers(resumption),
                OffStack: true, OffStackEffect: context.OffStackEffect),
            effect);
        return Result(run);
    }

    /// <summary>Which effect of the source asked, CR 614.1a and 603.7.</summary>
    private static Effect? Tree(GameState state, ChoiceContext context)
    {
        if (context.EffectPath is null)
            return null;
        if (context.OffStackEffect is { } saved)
            return saved;
        if (context.Source is not { } source)
            return Library.StandaloneLibrarySearch.Read(context.Extra);

        if (state.Objects.TryGetValue(source, out var obj) && context.AbilityIndex is { } index)
        {
            var abilities = obj.Face.AbilityList;
            if (index >= 0 && index < abilities.Count)
                return abilities[index] is ReplacementAbility replacement
                    ? replacement.With is ReplaceWith instead ? instead.Do : replacement.With
                    : Body(abilities[index]);
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

        // The asker said which object it was, so there is nothing to guess. Only a question
        // raised before this was recorded falls through to the scan below.
        if (context.Extra.TryGetValue(EffectInterpreter.AskingKey, out var named)
            && int.TryParse(named, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asking)
            && state.Objects.ContainsKey(new ObjectId(asking)))
        {
            return new ObjectId(asking);
        }
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
        var effect = EffectOf(obj, entry);
        if (effect is null)
            return new Outcome(state, [new StackObjectResolved(stackObject)]);

        var controller = characteristics.ControllerOf(state, stackObject);
        var context = Bind(Rechecked(state, obj, entry, controller, effect, resuming: true), resumption);

        var path = resumption.Context.EffectPath;
        var run = Execute(
            new EffectRun(state, context, [], false, "0", entry.AbilityIndex, path, resumption, path, Answers(resumption), StackObject: stackObject),
            effect);
        return run.Halted
            ? Result(run)
            : Result(run with { Events = run.Events.Add(new StackObjectResolved(stackObject)) });
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

        // CR 608.2: every answer this resolution has already given, not only the one just made.
        foreach (var (key, value) in Answers(resumption))
            context = context with { Chosen = context.Chosen.SetItem(key[AnsweredPrefix.Length..], value) };

        // CR 608.2h: what the steps before the question remembered, put back so a later step's
        // Remembered(key) still names it. The stack entry's own memory is the earlier word.
        foreach (var (key, value) in resumption.Context.Extra)
        {
            if (!key.StartsWith(RememberedPrefix, StringComparison.Ordinal))
                continue;
            var name = key[RememberedPrefix.Length..];
            if (!context.Remembered.ContainsKey(name))
                context = context with { Remembered = context.Remembered.SetItem(name, [.. Numbers(value).Select(n => new ObjectId(n))]) };
        }

        // CR 614.5: the replacement effects that had already applied to the event this walk is in
        // the middle of, so the resumed walk's re-proposal is not a fresh offer.
        return Restored(context, resumption.Context);
    }

    /// <summary>
    /// Every answer the walk being resumed has already been given, under the keys the arms that
    /// asked read them back by (CR 608.2). A resolution that asks twice — "its controller may
    /// search", and then which card — is resumed from the second question, which carries only the
    /// second answer; without the first the walk reaches the "you may" node again, asks it a second
    /// time, and the resolution never finishes, so the spell stays on the stack for ever. The
    /// answers ride on the question's own <see cref="ChoiceContext"/>, the way a loop's marks do,
    /// so there is still no continuation anywhere and a game rebuilt from its log resumes the same
    /// way (CR 101.4).
    /// </summary>
    [RuleRef("608.2")]
    private static ImmutableDictionary<string, string> Answers(Resumption resumption)
    {
        var answers = ImmutableDictionary<string, string>.Empty;
        foreach (var (key, value) in resumption.Context.Extra)
        {
            if (key.StartsWith(AnsweredPrefix, StringComparison.Ordinal))
                answers = answers.SetItem(key, value);
        }

        return resumption.Context.Extra.TryGetValue("storeAs", out var stored)
            ? answers.SetItem(AnsweredPrefix + stored, string.Join(",", resumption.Chosen.Select(o => o.Id)))
            : answers;
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
