using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Library;

public sealed record LibrarySearchCards(ImmutableList<ObjectId> Inspected, ImmutableList<ObjectId> Matching);

/// <summary>Searches an ordered set of card instances without changing or moving them.</summary>
public static class LibrarySearch
{
    public static LibrarySearchCards Find(ImmutableList<ObjectId> order, Func<ObjectId, bool> matches, int? limit = null)
    {
        // Limit the inspected prefix before applying the caller's matching policy.
        var inspected = limit is { } count ? order.Take(count).ToImmutableList() : order;
        return new LibrarySearchCards(inspected, inspected.Where(matches).ToImmutableList());
    }
}
