using System.Globalization;
using MtgEngine.Cards;
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
                        run = Execute(Descend(run, index++), step) with { Path = run.Path };
                        if (run.Halted)
                            break;
                    }

                    return run;
                }

            // CR 608.2a: an "if" inside an effect is checked as the effect resolves.
            case If test:
                return query.Holds(run.State, test.Test, run.Context)
                    ? Execute(Descend(run, 0), test.Then) with { Path = run.Path }
                    : test.Else is { } otherwise ? Execute(Descend(run, 1), otherwise) with { Path = run.Path } : run;

            // CR 608.2: "you may" is a question, and the game stops until it is answered.
            case May may:
                {
                    var who = FirstPlayer(run, may.Who);
                    if (may.StoreChoiceAs is { } key && run.Context.Chosen.TryGetValue(key, out var answered))
                        return Yes(answered) ? Execute(Descend(run, 0), may.Do) with { Path = run.Path } : run;
                    if (may.StoreChoiceAs is null && run.Context.Chosen.TryGetValue("may:" + run.Path, out var inline))
                        return Yes(inline) ? Execute(Descend(run, 0), may.Do) with { Path = run.Path } : run;
                    return Ask(run, who, "may", "You may do this.", YesNo, 1, 1, may.StoreChoiceAs ?? "may:" + run.Path);
                }

            // CR 118.8: the effect happens unless the named player pays; paying is a question too.
            case Unless unless:
                {
                    var who = FirstPlayer(run, unless.Who);
                    var key = "unless:" + run.Path;
                    if (run.Context.Chosen.TryGetValue(key, out var paid))
                        return Yes(paid) ? run : Execute(Descend(run, 0), unless.Do) with { Path = run.Path };
                    return Ask(run, who, "unless", "Pay the cost to stop this?", YesNo, 1, 1, key);
                }

            // CR 608.2: "for each" runs once per object, with that object as the triggering one.
            case ForEach each:
                {
                    var outer = run.Context;
                    var index = 0;
                    foreach (var id in Objects(run, each.Over))
                    {
                        run = run with { Context = run.Context with { Triggering = [id] } };
                        run = Execute(Descend(run, index++), each.Do) with { Path = run.Path };
                        if (run.Halted)
                            break;
                    }

                    return run with { Context = run.Context with { Triggering = outer.Triggering } };
                }

            case ForEachPlayer each:
                {
                    var outer = run.Context;
                    var index = 0;
                    foreach (var player in PlayersOrYou(run, each.Over))
                    {
                        run = run with { Context = run.Context with { TriggeringPlayer = player } };
                        run = Execute(Descend(run, index++), each.Do) with { Path = run.Path };
                        if (run.Halted)
                            break;
                    }

                    return run with { Context = run.Context with { TriggeringPlayer = outer.TriggeringPlayer } };
                }

            case Repeat repeat:
                {
                    var times = Math.Max(0, Number(run, repeat.Times));
                    for (var i = 0; i < times && !run.Halted; i++)
                        run = Execute(Descend(run, i), repeat.Do) with { Path = run.Path };
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
                        run = Execute(Descend(run, index), modal.Modes[index].Do) with { Path = run.Path };
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
                return Emit(run, new DelayedTriggerCreated(new DelayedTriggerState(
                    run.State.NextTimestamp,
                    delayed.When,
                    delayed.Do,
                    run.Context.Source,
                    run.Context.Controller,
                    delayed.Once,
                    run.Context.Remembered)));

            case DoNothing:
                return run;

            // The translator could not say this in the vocabulary; it must never reach a table.
            case Unsupported unsupported:
                throw new NotSupportedException(
                    $"the definition carries an {nameof(Unsupported)} effect and cannot resolve: {unsupported.Reason}");

            default:
                return null;
        }
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
