using MtgEngine.Rules.Library;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "Deck")]
public sealed class LibraryOrderInvariantTests
{
    [Fact]
    public void Arrangement_preserves_membership_with_repeated_or_stale_references()
    {
        ImmutableList<ObjectId> current = [new(1), new(2), new(3), new(4)];
        var ordered = LibraryOrder.Arrange(current, [new(3), new(3), new(99)], [new(2), new(2)]);
        Assert.Equal(new ObjectId[] { new(3), new(1), new(4), new(2) }, ordered);
        Assert.Equal(current.Count, ordered.Count);
        Assert.Equal(current.OrderBy(id => id.Value), ordered.OrderBy(id => id.Value));
    }
}
