using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// The structural arms, CR 608.2: how instructions are combined. Everything here either runs a
/// sub-effect, binds something into the context for the sub-effect to read, or stops the walk
/// with a question.
/// </summary>
public sealed partial class EffectInterpreter
{
    private EffectRun? Structure(EffectRun run, Effect effect)
    {
        switch (effect)
        {
            case Sequence sequence:
                {
                    var index = 0;
                    foreach (var step in sequence.Steps)
                    {
                        run = Child(run, index++, step);
                        if (run.Halted)
                            break;
                    }

                    return run;
                }

            // CR 608.2a: an "if" inside an effect is checked as the effect resolves.
            case If test:
                return query.Holds(run.State, test.Test, run.Context)
                    ? Child(run, 0, test.Then)
                    : test.Else is { } otherwise ? Child(run, 1, otherwise) : run;

            // CR 608.2: "you may" is a question, and the game stops until it is answered.
            case May may:
                {
                    var who = FirstPlayer(run, may.Who);
                    if (may.StoreChoiceAs is { } key && run.Context.Chosen.TryGetValue(key, out var answered))
                        return Yes(answered) ? Inside(run, key, may.Do) : run;
                    if (may.StoreChoiceAs is null && run.Context.Chosen.TryGetValue("may:" + run.Path, out var inline))
                        return Yes(inline) ? Inside(run, "may:" + run.Path, may.Do) : run;
                    return Ask(run, who, "may", "You may do this.", YesNo, 1, 1, may.StoreChoiceAs ?? "may:" + run.Path);
                }

            // CR 118.8: the effect happens unless the named player pays; paying is a question too.
            case Unless unless:
                {
                    var who = FirstPlayer(run, unless.Who);
                    var key = "unless:" + run.Path;
                    if (run.Context.Chosen.TryGetValue(key, out var paid))
                        return Yes(paid) ? run : Child(run, 0, unless.Do);
                    return Ask(run, who, "unless", "Pay the cost to stop this?", YesNo, 1, 1, key);
                }

            // CR 608.2: "for each" runs once per object, with that object as the triggering one.
            // The objects are worked out once and remembered (CR 101.4): a question raised in the
            // third iteration is answered against a state the first two changed, and re-resolving
            // the selector then would put a different object at the index the answer names.
            case ForEach each:
                {
                    var outer = run.Context;
                    ImmutableList<ObjectId> over = Marked(run) is { } resumed
                        ? [.. resumed.Select(id => new ObjectId(id))]
                        : Objects(run, each.Over);
                    run = Mark(run, over.Select(id => id.Value));
                    var index = 0;
                    foreach (var id in over)
                    {
                        run = run with { Context = run.Context with { Triggering = [id] } };
                        run = Child(run, index++, each.Do);
                        if (run.Halted)
                            break;
                    }

                    return Unmark(run) with { Context = run.Context with { Triggering = outer.Triggering } };
                }

            case ForEachPlayer each:
                {
                    var outer = run.Context;
                    ImmutableList<PlayerId> over = Marked(run) is { } resumed
                        ? [.. resumed.Select(seat => new PlayerId(seat))]
                        : PlayersOrYou(run, each.Over);
                    run = Mark(run, over.Select(player => player.Value));
                    var index = 0;
                    foreach (var player in over)
                    {
                        run = run with { Context = run.Context with { TriggeringPlayer = player } };
                        run = Child(run, index++, each.Do);
                        if (run.Halted)
                            break;
                    }

                    return Unmark(run) with { Context = run.Context with { TriggeringPlayer = outer.TriggeringPlayer } };
                }

            case Repeat repeat:
                {
                    var times = Math.Max(0, Number(run, repeat.Times));
                    for (var i = 0; i < times && !run.Halted; i++)
                        run = Child(run, i, repeat.Do);
                    return run;
                }

            // CR 700.2: the modes were chosen as the spell was cast; resolution just runs them.
            case ChooseMode modal:
                {
                    if (!run.Context.Chosen.TryGetValue("modes", out var chosen))
                        return Ask(
                            run,
                            run.Context.Controller,
                            "mode",
                            "Choose.",
                            [.. modal.Modes.Select((m, i) => new ChoiceOption(i.ToString(CultureInfo.InvariantCulture), m.Text))],
                            modal.Min,
                            modal.Max,
                            "modes");

                    foreach (var index in Indices(chosen))
                    {
                        if (index < 0 || index >= modal.Modes.Count)
                            continue;
                        run = Child(run, index, modal.Modes[index].Do);
                        if (run.Halted)
                            break;
                    }

                    return run;
                }

            case ChooseValue choose:
                return run.Context.Chosen.ContainsKey(choose.Key)
                    ? run
                    : Ask(
                        run,
                        FirstPlayer(run, choose.Who),
                        "value",
                        "Choose " + choose.What.ToString(),
                        [.. (choose.Options ?? []).Select(o => new ChoiceOption(o, o))],
                        1,
                        1,
                        choose.Key);

            case Remember remember:
                return Recall(run, remember.Key, Objects(run, remember.What));

            case Store store:
                return run with
                {
                    Context = run.Context with { Stored = run.Context.Stored.SetItem(store.Key, Number(run, store.Value, 0)) },
                };

            // CR 603.7: a delayed triggered ability is created now and triggers later, once.
            case DelayedTrigger delayed:
                return Delayed(run, delayed);

            case DoNothing:
                return run;

            // The translator could not say this in the vocabulary; it must never reach a table.
            // CR 608.2: the resolution cannot be performed, so the definition is refused by name
            // and with the reason the translator wrote, which a runner reports as a failure. A
            // thrown NotSupportedException was a crash at the engine's front door instead.
            case Unsupported unsupported:
                throw new RefusedException(
                    $"the definition carries an {nameof(Unsupported)} effect and cannot resolve: {unsupported.Reason}",
                    "608.2");

            default:
                return null;
        }
    }

    /// <summary>
    /// Create the delayed triggered ability, CR 603.7 — one of them, except for the case CR 106.6a
    /// names. An ability that produced keyed mana and then says "when that mana is spent" creates
    /// <em>a separate delayed ability for each mana produced</em>, so a replacement that made two
    /// mana where the card printed one gets two abilities and each fires for its own mana. Which
    /// mana each one watches is the key the <see cref="AddMana"/> wrote on that mana's provenance,
    /// so the two halves are linked through the pool and not through this node. An ability whose
    /// key produced no mana creates nothing to watch it with; a key the same resolution did not
    /// produce is left as the card wrote it, because CR 106.6 lets a linked ability elsewhere have
    /// made the mana.
    /// </summary>
    [RuleRef("603.7")]
    private EffectRun Delayed(EffectRun run, DelayedTrigger delayed)
    {
        if (delayed.When is ManaSpent spent && Keys(run.State, run.Context.Source, spent.ManaKey) is { Count: > 0 } keys)
        {
            foreach (var key in keys)
                run = Delaying(run, delayed, spent with { ManaKey = key });
            return run;
        }

        return Delaying(run, delayed, delayed.When);
    }

    [RuleRef("603.7")]
    private static EffectRun Delaying(EffectRun run, DelayedTrigger delayed, Trigger when) =>
        Emit(run, new DelayedTriggerCreated(new DelayedTriggerState(
            run.State.NextTimestamp,
            when,
            delayed.Do,
            run.Context.Source,
            run.Context.Controller,
            delayed.Once,
            run.Context.Remembered)));

    /// <summary>
    /// The mana this source produced under that key, CR 106.6a, one entry per single mana. Empty
    /// when nothing in any pool carries it: the mana has been spent already, or another permanent's
    /// linked ability is the one that will make it (CR 106.6).
    /// </summary>
    [RuleRef("106.6a")]
    private static ImmutableList<string> Keys(GameState state, ObjectId? source, string key)
    {
        if (source is null)
            return [];

        var prefix = key + "#";
        var keys = ImmutableList.CreateBuilder<string>();
        foreach (var player in state.Players)
        {
            foreach (var block in player.Mana.Provenance)
            {
                if (block.Source != source || block.Key is not { } written || !written.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                for (var i = 0; i < block.Amount; i++)
                    keys.Add(written);
            }
        }

        return keys.ToImmutable();
    }

    /// <summary>
    /// Run what a "you may" says yes to, telling the walk which answer it is inside. A
    /// <see cref="PayCost"/> in there that cannot be paid puts that answer back to no, so the
    /// "if you do" half reads false rather than running on an unpaid cost (CR 118.12).
    /// </summary>
    [RuleRef("118.12")]
    private EffectRun Inside(EffectRun run, string key, Effect body)
    {
        var inner = Child(run with { PayKey = key }, 0, body);
        return inner with { PayKey = run.PayKey };
    }

    private static bool Yes(string answer) => answer is "yes" or "true" or "1";

    private static IEnumerable<int> Indices(string chosen) => chosen
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => int.TryParse(s, CultureInfo.InvariantCulture, out var n) ? n : -1);

    /// <summary>
    /// The path of the sub-effect about to run. A pending question records it, so resuming is a
    /// branch on the kind and the path, never a captured continuation.
    /// </summary>
    private static EffectRun Descend(EffectRun run, int index) =>
        run with { Path = run.Path + "." + index.ToString(CultureInfo.InvariantCulture) };
}
