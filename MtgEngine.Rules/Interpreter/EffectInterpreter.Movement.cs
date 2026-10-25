using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

public sealed partial class EffectInterpreter
{
    private EffectRun MoveObjects(EffectRun run, Move move)
    {
        run = OrderLibraryMovement(run, move);
        if (run.Halted)
            return run;
        var arrived = run.Context.Remembered.GetValueOrDefault(":arrived:" + run.Path, []).ToList();
        for (var index = 0; index < MovementObjects(ref run, move.What).Count; index++)
        {
            var id = MovementObjects(ref run, move.What)[index];
            var controller = move.UnderControlOf is null ? (PlayerId?)null : FirstPlayer(run, move.UnderControlOf);
            (run, var landed) = Relocate(run, id, move.To, move.Where, move.Tapped, controller);
            if (run.Halted)
                return run;
            if (landed is { } now && !arrived.Contains(now))
            {
                arrived.Add(now);
                run = Recall(run, ":arrived:" + run.Path, arrived);
            }
        }
        return ForgetMovement(Recall(run, move.RememberAs, arrived));
    }

    private EffectRun OrderLibraryMovement(EffectRun run, Move move)
    {
        if (move.To != Zone.Library || !move.OwnersChoiceOfOrder)
            return run;
        var prefix = ":move-order:" + run.Path + ":";
        if (run.Marks?.ContainsKey(prefix) == true)
            return run;
        var ids = MovementObjects(ref run, move.What);
        var groups = ids.Where(run.State.Objects.ContainsKey).GroupBy(id => run.State.Objects[id].Owner)
            .ToDictionary(group => group.Key, group => group.ToImmutableList());
        var ordered = ImmutableList<ObjectId>.Empty;
        foreach (var owner in InTurnOrder(run.State, groups.Keys))
        {
            var key = prefix + owner.Value;
            if (!run.Context.Remembered.TryGetValue(key, out var chosen))
            {
                chosen = groups[owner];
                if (chosen.Count > 1)
                {
                    var answer = run.Answer?.Player == owner ? Answered(run) : null;
                    if (answer is null)
                        return Ask(run, owner, move.Where == Placement.Bottom ? ChoiceKinds.LibraryBottomOrder : ChoiceKinds.LibraryTopOrder,
                            "Order these cards from top to bottom within their group.", OptionsFor(run.State, chosen), chosen.Count, chosen.Count);
                    chosen = Named(run.State, answer);
                }
                run = Recall(run, key, chosen);
            }
            // Individual top insertions reverse their input; bottom insertions append.
            ordered = ordered.AddRange(move.Where == Placement.Bottom ? chosen : chosen.Reverse());
        }
        run = Recall(run, ":moving:" + run.Path, ordered);
        return run with { Marks = (run.Marks ?? ImmutableDictionary<string, string>.Empty).SetItem(prefix, "1") };
    }

    /// <summary>CR 608.2: preserve a paused instruction's object list and proposal ordinals.</summary>
    [RuleRef("608.2")]
    private ImmutableList<ObjectId> MovementObjects(ref EffectRun run, Selector selector)
    {
        var key = ":moving:" + run.Path;
        if (run.Context.Remembered.TryGetValue(key, out var saved))
            return saved;
        var objects = Objects(run, selector);
        run = Recall(run, key, objects);
        return objects;
    }

    private static EffectRun ForgetMovement(EffectRun run) => run with
    {
        Marks = run.Marks?.Remove(":move-order:" + run.Path + ":"),
        Context = run.Context with
        {
            Remembered = run.Context.Remembered.Remove(":moving:" + run.Path).Remove(":arrived:" + run.Path)
                .RemoveRange(run.Context.Remembered.Keys.Where(key => key.StartsWith(":move-order:" + run.Path + ":", StringComparison.Ordinal))),
        },
    };
}
