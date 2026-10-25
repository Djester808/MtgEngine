using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

public sealed partial class EffectInterpreter
{
    /// <summary>Keep the evaluated draw instruction stable while individual replacements pause it.</summary>
    private EffectRun Drawing(EffectRun run, Draw draw)
    {
        var key = ":draw-plan:" + run.Path;
        var plan = run.Answer?.Context.Extra.TryGetValue(key, out var saved) == true
            ? Numbers(saved).ToList()
            : new List<int>();
        if (plan.Count == 0)
        {
            plan.Add(Math.Max(0, Number(run, draw.N)));
            // CR 121.2c: all of the active player's draws, then each next player in turn order.
            plan.AddRange(InTurnOrder(run.State, PlayersOrYou(run, draw.Who)).Select(player => player.Value));
        }
        run = run with
        {
            Marks = (run.Marks ?? ImmutableDictionary<string, string>.Empty).SetItem(key, Joined(plan)),
        };
        foreach (var player in plan.Skip(1))
        {
            for (var index = 0; index < plan[0]; index++)
            {
                run = ProposeThrough(run, current => zones.Draw(current.State, new PlayerId(player), 1, current.Context.UsedReplacements));
                if (run.Halted)
                    return run;
            }
        }
        return run with { Marks = run.Marks?.Remove(key) };
    }
}
