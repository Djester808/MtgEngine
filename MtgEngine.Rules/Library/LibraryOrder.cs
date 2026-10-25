using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Library;

/// <summary>Arrange known cards at either end without changing objects or disturbing the rest.</summary>
public static class LibraryOrder
{
    /// <summary>Reposition one existing card; negative or oversized indices mean the bottom.</summary>
    public static ImmutableList<ObjectId> Place(ImmutableList<ObjectId> current, ObjectId card, int position)
    {
        if (!current.Contains(card))
            return current;
        var rest = current.Remove(card);
        return rest.Insert(position < 0 || position > rest.Count ? rest.Count : position, card);
    }

    public static ImmutableList<ObjectId> Arrange(
        ImmutableList<ObjectId> current,
        ImmutableList<ObjectId> top,
        ImmutableList<ObjectId> bottom)
    {
        var placed = top.Concat(bottom).ToHashSet();
        return top.Where(current.Contains)
            .Concat(current.Where(id => !placed.Contains(id)))
            .Concat(bottom.Where(current.Contains))
            .Distinct().ToImmutableList();
    }
}
