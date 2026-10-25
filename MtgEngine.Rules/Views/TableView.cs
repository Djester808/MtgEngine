using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Views;

/// <summary>
/// Presentation references for a two-to-four-player table. The source is already redacted;
/// this projection never reads hidden game state or decides which actions are legal.
/// Opponents are in circular seat order after the viewer, ready to render across the top.
/// </summary>
public sealed record TableView(
    TableSeatView Self,
    ImmutableList<TableSeatView> Opponents,
    ImmutableList<ObjectId> StackTopFirst)
{
    public static TableView From(GameView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.Players.Count is < 2 or > 4)
            throw new ArgumentException("A table requires two to four players.", nameof(view));
        var viewerIndex = view.Players.FindIndex(p => p.Id == view.Viewer);
        if (viewerIndex < 0 || view.Players.Select(p => p.Id).Distinct().Count() != view.Players.Count)
            throw new ArgumentException("A table requires distinct seats including the viewer.", nameof(view));

        var seats = Enumerable.Range(0, view.Players.Count)
            .Select(offset => (viewerIndex + offset) % view.Players.Count)
            .Select(index => Seat(view, index))
            .ToImmutableList();
        return new TableView(seats[0], seats.RemoveAt(0), [.. view.Stack.Reverse().Select(o => o.Id)]);
    }

    private static TableSeatView Seat(GameView view, int index)
    {
        var id = view.Players[index].Id;
        var controlled = view.Battlefield.Where(o => o.Controller == id).ToImmutableList();
        // A land creature belongs in the creature row. These are layout hints, not zones.
        var creatures = controlled.Where(o => HasType(o, "Creature")).Select(o => o.Id).ToImmutableList();
        var lands = controlled.Where(o => !HasType(o, "Creature") && HasType(o, "Land"))
            .Select(o => o.Id).ToImmutableList();
        var other = controlled.Where(o => !HasType(o, "Creature") && !HasType(o, "Land"))
            .Select(o => o.Id).ToImmutableList();
        return new TableSeatView(id, index, new BattlefieldRows(creatures, lands, other),
            [.. view.Exile.Where(o => o.Owner == id).Select(o => o.Id)]);
    }

    private static bool HasType(ObjectView obj, string type) =>
        obj.Types.Contains(type, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Seat is stable in turn order; rotating the viewer changes only screen position.</summary>
public sealed record TableSeatView(
    PlayerId Player,
    int Seat,
    BattlefieldRows Battlefield,
    ImmutableList<ObjectId> OwnedExile);

/// <summary>
/// Disjoint references into GameView.Battlefield. The client displays opponent lands above
/// creatures and reverses that arrangement for self. Pixel positions, piles, hand size, and
/// collapsed panels remain local presentation state, never rules state.
/// </summary>
public sealed record BattlefieldRows(
    ImmutableList<ObjectId> Creatures,
    ImmutableList<ObjectId> Lands,
    ImmutableList<ObjectId> OtherPermanents);
