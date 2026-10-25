using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

public sealed partial class EffectInterpreter
{
    // Reordering may reset revealed objects without moving their physical cards. Only
    // this operation's own bookkeeping follows that transition.
    private static EffectRun RefreshLibraryIdentities(EffectRun run, string memory, ZoneReordered reordered,
        string operation, IEnumerable<PlayerId> viewers)
    {
        var changed = ReorderedIdentities(run, reordered);
        if (changed.Count == 0)
            return run;
        var remembered = run.Context.Remembered;
        foreach (var (key, ids) in remembered.Where(pair => pair.Key.StartsWith(memory, StringComparison.Ordinal)))
            remembered = remembered.SetItem(key, ids.Select(id => changed.GetValueOrDefault(id, id)).ToImmutableList());
        run = run with { Context = run.Context with { Remembered = remembered } };
        foreach (var viewer in viewers)
        {
            var scope = LibraryInspectionScope(run, operation, viewer);
            if (run.State.LibraryInspections.TryGetValue(scope, out var inspection))
                run = InspectLibrary(run, operation, viewer, inspection.Owner,
                    inspection.Cards.Select(id => changed.GetValueOrDefault(id, id)).ToImmutableList());
        }
        return run;
    }

    private static ImmutableDictionary<ObjectId, ObjectId> ReorderedIdentities(EffectRun run, ZoneReordered reordered) =>
        Library.LibraryIdentity.AfterReorder(reordered.Order, run.State.In(reordered.Zone));

    private static EffectRun RefreshDisclosedLibraryIdentities(EffectRun run, ZoneReordered reordered)
    {
        var changed = ReorderedIdentities(run, reordered);
        if (changed.Count == 0)
            return run;
        var remembered = run.Context.Remembered.ToImmutableDictionary(pair => pair.Key,
            pair => pair.Value.Select(id => changed.GetValueOrDefault(id, id)).ToImmutableList());
        run = run with { Context = run.Context with { Remembered = remembered } };
        var resolution = DisclosureResolution(run);
        foreach (var (scope, inspection) in run.State.LibraryInspections
            .Where(pair => pair.Value.Resolution == resolution).ToList())
            run = Emit(run, new LibraryInspectionChanged(scope, inspection with
            {
                Cards = inspection.Cards.Select(id => changed.GetValueOrDefault(id, id)).ToImmutableList(),
            }));
        return run;
    }
}
