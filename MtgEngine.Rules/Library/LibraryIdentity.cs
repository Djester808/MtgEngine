using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Library;

/// <summary>Tracks identity resets caused by revealing or hiding cards during an order change.</summary>
public static class LibraryIdentity
{
    /// <summary>
    /// Compare the requested order with its immediately applied result. These lists describe
    /// the same physical cards in the same positions; only their object identities may differ.
    /// A different card count cannot establish that correspondence.
    /// </summary>
    public static ImmutableDictionary<ObjectId, ObjectId> AfterReorder(
        ImmutableList<ObjectId> requested, ImmutableList<ObjectId> applied) =>
        requested.Count != applied.Count ? ImmutableDictionary<ObjectId, ObjectId>.Empty
            : requested.Zip(applied).Where(pair => pair.First != pair.Second)
                .ToImmutableDictionary(pair => pair.First, pair => pair.Second);
}
