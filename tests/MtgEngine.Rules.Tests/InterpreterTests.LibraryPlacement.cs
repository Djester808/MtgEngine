using MtgEngine.Cards;
using MtgEngine.Rules.Events;

namespace MtgEngine.Rules.Tests;

public sealed partial class InterpreterTests
{
    [Theory]
    [InlineData(Placement.Top)]
    [InlineData(Placement.Bottom)]
    public void Search_places_found_library_card_after_shuffle_without_creating_a_new_object(Placement placement)
    {
        var table = new InterpreterTable();
        var (land, creature) = Stocked(table);
        table.Card(table.A, Zone.Library);
        table.Resolve(table.OnStack(table.A,
            new Search(ObjectFilter.Any, Zone.Library, Where: placement, Reveal: true)));
        var events = table.Answer(Option(table, creature));
        var library = table.In(Zone.Library, table.A);
        Assert.Equal(creature, placement == Placement.Top ? library[0] : library[^1]);
        Assert.Contains(land, library);
        Assert.Equal(3, library.Count);
        Assert.DoesNotContain(events, e => e is ObjectMoved);
        Assert.Single(events.OfType<ZoneReordered>(), e => e.Shuffle);
        Assert.Single(events.OfType<CardsRevealed>());
    }
}
