using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Library;

/// <summary>Offers a shuffle before consuming randomness; commits it after replacement decisions.</summary>
public sealed class LibraryShuffleCoordinator(IReplacementSystem? replacements)
{
    public Outcome Shuffle(GameState state, PlayerId player, IEnumerable<string>? usedReplacements = null)
    {
        var proposed = new ZoneReordered(ZoneKey.Of(Cards.Zone.Library, player), [], Shuffle: true)
        { AwaitingRandomization = true };
        var offered = replacements?.Replace(state, proposed, player, usedReplacements) ?? new Outcome(state, [proposed]);
        var result = Outcome.Unchanged(state);
        foreach (var item in offered.Events)
            result = result.Then(current => item is ZoneReordered { AwaitingRandomization: true } shuffle
                ? Complete(current, shuffle) : new Outcome(item.Apply(current), [item]));
        return result;
    }

    internal static Outcome Complete(GameState state, ZoneReordered proposed) =>
        LibraryShuffle.Apply(state, proposed.Zone);
}
