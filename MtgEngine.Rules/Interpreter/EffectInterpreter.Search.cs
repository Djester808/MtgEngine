using MtgEngine.Cards;
using MtgEngine.Rules.Library;

namespace MtgEngine.Rules.Interpreter;

public sealed partial class EffectInterpreter
{
    private readonly LibrarySearchPlanner searchPlanner = new(query, characteristics);

    private EffectRun Searching(EffectRun run, Search search)
    {
        var runtime = new SearchRuntime(this, zones, run);
        new LibrarySearchCoordinator(runtime).Execute(search);
        return runtime.Result;
    }

    /// <summary>An optional search cannot be chosen when its search instruction is prohibited.</summary>
    private bool OptionalSearchProhibited(EffectRun run, Effect effect) => effect switch
    {
        Search search => PlayersOrYou(run, search.Who).All(player => searchPlanner.AccessibleZones(run.State, search, player).IsEmpty),
        Sequence sequence when sequence.Steps.Count > 0 => OptionalSearchProhibited(run, sequence.Steps[0]),
        _ => false,
    };

}
