using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Library;

/// <summary>Moves the current top card to its owner's hand and records one draw.</summary>
public static class LibraryDraw
{
    public static Outcome Apply(GameState state, PlayerId player)
    {
        if (ZoneSystem.PrepareDraw(state, player) is not { } movement)
        {
            var empty = new DrawFromEmptyLibraryAttempted(player);
            return new Outcome(empty.Apply(state), [empty]);
        }
        var drawn = new ObjectDrawn(player, movement.To.Id);
        return new Outcome(drawn.Apply(movement.Apply(state)), [movement, drawn]);
    }
}
