using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

public sealed partial class EffectInterpreter
{
    private static string DisclosureResolution(EffectRun run) =>
        $"disclosure:{run.StackObject}:{run.Context.Source}:{run.AbilityIndex}:{run.OffStack}";

    private static EffectRun InspectDisclosedLibraries(EffectRun run, IEnumerable<ObjectId> cards,
        IEnumerable<PlayerId> viewers, bool publicReveal = false)
    {
        var groups = cards.Distinct().Where(run.State.Objects.ContainsKey)
            .Select(id => run.State.Objects[id]).Where(card => card.Zone.Zone == Zone.Library)
            .GroupBy(card => card.Owner).ToList();
        foreach (var viewer in viewers.Distinct().Where(viewer => !run.State.Player(viewer).HasLost))
        {
            foreach (var group in groups)
            {
                var resolution = DisclosureResolution(run);
                var scope = $"{resolution}:{run.Path}:{viewer.Value}:{group.Key.Value}";
                run = Emit(run, new LibraryInspectionChanged(scope,
                    new LibraryInspection(viewer, group.Key, group.Select(card => card.Id).ToImmutableList())
                    { Resolution = resolution, PublicReveal = publicReveal }));
            }
        }
        return run;
    }

    private static EffectRun FinishDisclosures(EffectRun run)
    {
        if (run.Halted || run.Path != "0")
            return run;
        var resolution = DisclosureResolution(run);
        foreach (var scope in run.State.LibraryInspections
            .Where(pair => pair.Value.Resolution == resolution).Select(pair => pair.Key).ToList())
            run = Emit(run, new LibraryInspectionChanged(scope, null));
        return run;
    }

    private static string LibraryInspectionScope(EffectRun run, string operation, PlayerId viewer) =>
        $"{operation}:{run.Context.Source}:{run.AbilityIndex}:{run.Path}:{viewer.Value}";

    private static EffectRun InspectLibrary(EffectRun run, string operation, PlayerId viewer,
        PlayerId owner, ImmutableList<ObjectId> cards) => cards.IsEmpty ? run
        : Emit(run, new LibraryInspectionChanged(LibraryInspectionScope(run, operation, viewer),
            new LibraryInspection(viewer, owner, cards)));

    private static EffectRun EndLibraryInspection(EffectRun run, string operation, PlayerId viewer)
    {
        var scope = LibraryInspectionScope(run, operation, viewer);
        return run.State.LibraryInspections.ContainsKey(scope)
            ? Emit(run, new LibraryInspectionChanged(scope, null)) : run;
    }
}
