using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Library;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "Deck")]
public sealed class ManualLibraryMovementTests
{
    [Fact]
    public void Full_arrangement_refuses_a_card_added_after_inspection()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        var inspection = ManualLibraryInspection.Set(table.State, table.A, true).Events;
        table.Card(table.A, Zone.Library);
        var state = GameReducer.Apply(table.State, inspection.AddRange(
            [new TurnBegan(1, table.A, false), new StepBegan(Step.PrecombatMain), new PriorityGranted(table.A)]));
        var order = state.In(ZoneKey.Of(Zone.Library, table.A)).Reverse().ToImmutableList();
        var game = new Game(Subsystems.Wave2(), new Definitions());
        var command = new ManualLibraryArrange(table.A, Guid.NewGuid(), order);
        Assert.IsType<Refused>(game.Apply(state, command));
        state = ManualLibraryInspection.Set(state, table.A, true).State;
        var result = Assert.IsType<Applied>(game.Apply(state, command));
        Assert.Equal(order, result.State.In(ZoneKey.Of(Zone.Library, table.A)));
        Assert.Empty(result.Events.OfType<ObjectMoved>());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(-1, 2)]
    [InlineData(99, 2)]
    public void Repositioning_preserves_hidden_objects_and_replays_without_a_zone_change(int position, int expectedIndex)
    {
        var table = new InterpreterTable();
        var library = ZoneKey.Of(Zone.Library, table.A);
        for (var i = 0; i < 3; i++)
            table.Card(table.A, Zone.Library);
        var before = ManualLibraryInspection.Set(table.State, table.A, true).State;
        before = new LibraryInspectionChanged("effect-inspection",
            new LibraryInspection(table.A, table.A, before.In(library))).Apply(before);
        var cards = before.In(library);
        var moved = cards[1];
        var game = new Game(Subsystems.Wave2(), new Definitions());

        var result = Assert.IsType<Applied>(game.Apply(before,
            new ManualMove(table.A, moved, library, position)));

        Assert.Empty(result.Events.OfType<ObjectMoved>());
        Assert.Single(result.Events.OfType<ManualActionTaken>());
        var expected = cards.Remove(moved).Insert(expectedIndex, moved);
        Assert.Equal(expected, result.State.In(library));
        Assert.Equal(before.NextObjectId, result.State.NextObjectId);
        Assert.Equal(expectedIndex == 1, ManualLibraryInspection.IsActive(result.State, table.A));
        Assert.Equal(before.LibraryInspections["effect-inspection"], result.State.LibraryInspections["effect-inspection"]);
        foreach (var id in cards)
            Assert.Equal(before.Objects[id], result.State.Objects[id]);
        var replay = GameReducer.Apply(before, EventLogSerializer.Read(EventLogSerializer.Write(result.Events)));
        Assert.Equal(expected, replay.In(library));
        Assert.Equal(expectedIndex == 1, ManualLibraryInspection.IsActive(replay, table.A));
        Assert.Equal(before.LibraryInspections["effect-inspection"], replay.LibraryInspections["effect-inspection"]);
        foreach (var id in cards)
            Assert.Equal(before.Objects[id], replay.Objects[id]);
    }

    private sealed class Definitions : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => null;
        public CardDefinition? ByOracleId(string oracleId) => null;
    }
}
