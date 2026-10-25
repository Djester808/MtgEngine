using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;

namespace MtgEngine.Rules.Tests;

public sealed class LibraryActionTriggerTests
{
    [Fact]
    public void Identical_search_events_retain_distinct_batch_positions_after_serialization()
    {
        var table = new InterpreterTable();
        table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new TriggeredAbility(new Searches(new PlayerFilter(IsYou: true)), new GainLife(new Fixed(1)))));
        var found = Subsystems.Wave2().Triggers.Detect(table.State,
            [new LibrarySearched(table.A, table.A), new LibrarySearched(table.A, table.A)]);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ImmutableList<PendingTrigger>>(
            System.Text.Json.JsonSerializer.Serialize(found, EventLogSerializer.Options), EventLogSerializer.Options)!;
        Assert.Equal(2, restored.Count);
        Assert.Equal(new int?[] { 0, 1 }, restored.Select(trigger => trigger.CauseIndex));
        Assert.All(restored, trigger => Assert.IsType<LibrarySearched>(trigger.Cause));
        Assert.Equal(restored[0].Source, restored[1].Source);
    }

}
