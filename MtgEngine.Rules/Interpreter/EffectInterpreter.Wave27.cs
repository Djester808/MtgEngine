using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// Wave 27: moving counters as one action (CR 122.5), including the count the controller chooses
/// as the effect resolves, and the placement a countered spell is put into instead of a graveyard
/// (CR 701.6a). Both are written from the rule: a move is not a removal followed by a putting, and
/// a counter that names a placement puts the spell there and nowhere else on the way.
/// </summary>
public sealed partial class EffectInterpreter
{
    /// <summary>
    /// CR 122.5: moving a counter from one object to another. The whole record is one action — if
    /// either half is impossible (nothing to take a counter from, nothing to put one on), then
    /// <b>nothing</b> moves and no counter is removed, which is why the removal here happens only
    /// after the putting has actually landed. A remove-then-put pair would leave the counters gone
    /// when the second half was replaced away.
    /// <para>
    /// <c>AnyNumber</c> is the controller's own choice, made as the effect resolves and bounded by
    /// what is actually there (CR 122.5 with 601.2b): none up to every counter the record admits.
    /// <c>All</c> is "all of them" and <c>N</c> is a printed count; the three are three different
    /// lines and at most one is set (L044).
    /// </para>
    /// </summary>
    [RuleRef("122.5")]
    private EffectRun Moved(EffectRun run, MoveCounters move)
    {
        var from = Objects(run, move.From).Where(run.State.Objects.ContainsKey).ToImmutableList();
        var onto = Objects(run, move.To).Where(run.State.Objects.ContainsKey).ToImmutableList();

        // CR 122.5: neither half can happen on its own, so a move with no source or no destination
        // does nothing at all rather than removing counters that land nowhere.
        if (from.Count == 0 || onto.Count == 0)
            return run;

        // CR 122.5 with 601.2c: the record says which object the counters go onto. More than one
        // destination is a division this vocabulary cannot say, and is refused with the rule
        // instead of being guessed at by putting every counter on whichever object came first.
        if (onto.Count > 1)
        {
            throw new RefusedException(
                "counters were to be moved onto more than one object, and the effect does not say how they are divided",
                "122.5");
        }

        var available = Movable(run.State, from, move.Counter);
        if (available.Count == 0)
            return run;

        var total = available.Sum(pair => pair.Value);
        var moving = total;
        if (move.AnyNumber)
        {
            // CR 122.5 with 601.2b: "move any number of counters" is a number the controller
            // announces as the effect resolves, from none up to what is there — so the walk stops
            // and asks, and the answer comes back here to move exactly that many.
            if (Answered(run) is not { } answer)
            {
                return Ask(
                    run,
                    run.Context.Controller,
                    ChoiceKinds.Value,
                    "Choose how many counters to move.",
                    NumberOptions(0, total),
                    1,
                    1);
            }

            moving = answer
                .Select(option => int.TryParse(option.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .Max();
            moving = Math.Clamp(moving, 0, total);
        }
        else if (!move.All)
        {
            moving = move.N is null ? 1 : Math.Max(0, Number(run, move.N));
        }

        if (moving <= 0)
            return run;

        var destination = onto[0];
        foreach (var (source, kind, held) in Ordered(available))
        {
            if (moving <= 0)
                break;
            var count = Math.Min(held, moving);

            // CR 614.1 with 122.5: putting counters on is the replaceable half ("enters with an
            // additional counter", "if one or more counters would be put on"), so it is proposed
            // first; when it is replaced away nothing is taken off the object they came from.
            var landed = run.Events.Count;
            run = Propose(run, new CountersChanged(destination, kind, count), characteristics.ControllerOf(run.State, destination));
            if (run.Halted)
                return run;
            if (!run.Events.Skip(landed).OfType<CountersChanged>().Any(e => e.Id == destination && e.Kind == kind))
                continue;

            run = Emit(run, new CountersChanged(source, kind, -count));
            moving -= count;
        }

        return run;
    }

    /// <summary>
    /// CR 122.5: what the record admits, per object and per kind. A null kind is every kind the
    /// object carries; a named kind is that one and never a similar one.
    /// </summary>
    [RuleRef("122.5")]
    private static ImmutableDictionary<(ObjectId Object, string Kind), int> Movable(
        GameState state,
        IEnumerable<ObjectId> from,
        string? kind)
    {
        var held = ImmutableDictionary.CreateBuilder<(ObjectId, string), int>();
        foreach (var id in from)
        {
            if (!state.Objects.TryGetValue(id, out var obj))
                continue;
            foreach (var (counter, count) in obj.Counters)
            {
                if (count > 0 && (kind is null || string.Equals(counter, kind, StringComparison.Ordinal)))
                    held[(id, counter)] = count;
            }
        }

        return held.ToImmutable();
    }

    /// <summary>The counters to take, in a fixed order so a partial move is the same every run.</summary>
    private static IEnumerable<(ObjectId Source, string Kind, int Held)> Ordered(
        ImmutableDictionary<(ObjectId Object, string Kind), int> available) =>
        available
            .OrderBy(pair => pair.Key.Object.Value)
            .ThenBy(pair => pair.Key.Kind, StringComparer.Ordinal)
            .Select(pair => (pair.Key.Object, pair.Key.Kind, pair.Value));
}
