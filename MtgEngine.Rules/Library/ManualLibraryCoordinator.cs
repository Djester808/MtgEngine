using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Library;

/// <summary>Logged, retry-safe manual library instructions using the same draw/shuffle operations as effects.</summary>
public sealed class ManualLibraryCoordinator(IZoneSystem zones, ILibrarySearchInteraction searchInteraction)
{
    public Outcome Execute(GameState state, ManualLibraryCommand command)
    {
        if (command.RequestId == Guid.Empty)
            throw new RefusedException("A library action needs a request ID.");
        if (state.ManualRequests.Contains(new ManualRequest(command.Player, command.RequestId)))
            throw new RefusedException("That library action was already applied.");
        if (!ManualLibraryPolicy.IsAvailable(state, command.Player))
            throw new RefusedException("Library actions are unavailable right now.");
        var action = command switch
        {
            ManualLibraryDraw => "draw",
            ManualLibraryShuffle => "shuffle",
            ManualLibraryLook => "look",
            ManualLibraryStopLooking => "stop looking",
            ManualLibraryReposition => "reposition",
            ManualLibrarySearch => "search",
            ManualLibraryArrange => "arrange",
            _ => throw new RefusedException("Unknown library action."),
        };
        var recorded = new ManualActionTaken(command.Player, action, null, "Own library") { RequestId = command.RequestId };
        return new Outcome(recorded.Apply(state), [recorded]).Then(current => command switch
        {
            ManualLibraryDraw => zones.Draw(current, command.Player, 1),
            ManualLibraryShuffle => zones.Shuffle(current, command.Player),
            ManualLibraryLook => ManualLibraryInspection.Set(current, command.Player, true),
            ManualLibraryStopLooking => ManualLibraryInspection.Set(current, command.Player, false),
            ManualLibraryReposition move => Reposition(current, move),
            ManualLibrarySearch search => Search(current, search),
            ManualLibraryArrange arrange => Arrange(current, arrange),
            _ => throw new RefusedException("Unknown library action."),
        });
    }

    private Outcome Search(GameState state, ManualLibrarySearch command)
    {
        if (command.Count < 0 || command.Destination is not (Cards.Zone.Hand or Cards.Zone.Battlefield
            or Cards.Zone.Graveyard or Cards.Zone.Exile or Cards.Zone.Library)
            || command.Where is not (Cards.Placement.Default or Cards.Placement.Top or Cards.Placement.Bottom))
            throw new RefusedException("Invalid library search instructions.");
        return searchInteraction.Begin(state, command);
    }

    private static Outcome Reposition(GameState state, ManualLibraryReposition command)
    {
        if (!ManualLibraryInspection.Contains(state, command.Player, command.Card))
            throw new RefusedException("Look at your library before repositioning one of its cards.");
        var library = ZoneKey.Of(Cards.Zone.Library, command.Player);
        var changed = new ZoneReordered(library, LibraryOrder.Place(state.In(library), command.Card, command.Position));
        return new Outcome(changed.Apply(state), [changed]);
    }

    private static Outcome Arrange(GameState state, ManualLibraryArrange command)
    {
        var library = ZoneKey.Of(Cards.Zone.Library, command.Player);
        var current = state.In(library);
        if (!ManualLibraryInspection.IsActive(state, command.Player))
            throw new RefusedException("Library inspection ended. Look again.");
        if (!ManualLibraryInspection.CanArrange(state, command.Player, command.Order))
            throw new RefusedException("Arrange every card in your currently inspected library exactly once.");
        var changed = new ZoneReordered(library, LibraryOrder.Arrange(current, command.Order, []));
        return new Outcome(changed.Apply(state), [changed]);
    }
}
