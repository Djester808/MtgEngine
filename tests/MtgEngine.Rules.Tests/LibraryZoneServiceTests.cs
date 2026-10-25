using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "Deck")]
public sealed class LibraryZoneServiceTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void A_batch_keeps_following_a_reidentified_revealed_card(int interruptionIndex)
    {
        var table = new InterpreterTable();
        var engine = Subsystems.Wave2();
        var library = ZoneKey.Of(Zone.Library, table.A);
        for (var i = 0; i < 3; i++)
            table.Put(table.A, library, InterpreterTable.Definition(CardType.Land) with { OracleId = $"card-{i}" });
        var state = table.State;
        var cards = state.In(library);
        state = new Library.LibraryRevealTrackingChanged(
            ImmutableDictionary<PlayerId, ObjectId>.Empty.Add(table.A, cards[0]), []).Apply(state);
        var requests = new List<MoveRequest>
        {
            new(cards[1], library, Placement.Bottom),
            new(cards[0], library, Placement.Bottom),
        };
        if (interruptionIndex >= 0)
        {
            var commander = new GameObject(new ObjectId(state.NextObjectId),
                InterpreterTable.Definition(CardType.Creature), 0, table.A, table.A,
                ZoneKey.Shared(Zone.Battlefield), state.NextTimestamp)
            { IsCommander = true };
            state = new ObjectCreated(commander, -1).Apply(state);
            requests.Insert(interruptionIndex, new MoveRequest(commander.Id, ZoneKey.Of(Zone.Hand, table.A)));
        }
        var before = state;
        var result = engine.Zones.MoveAll(state, requests);
        var events = result.Events;
        state = GameReducer.Apply(before, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        if (interruptionIndex >= 0)
        {
            var pending = Assert.IsType<PendingChoice>(state.Pending);
            result = engine.Choices.Answer(state, new AnswerChoice(table.A, pending.Id, ["keep"]));
            events = events.AddRange(result.Events);
            state = GameReducer.Apply(before, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        }
        Assert.Null(state.Pending);
        Assert.Equal(new[] { "card-2", "card-1", "card-0" },
            state.In(library).Select(id => state.Objects[id].Definition.OracleId));
        Assert.DoesNotContain(cards[0], state.Objects.Keys);
        Assert.Equal(2, events.OfType<ZoneReordered>().Count());
        Assert.DoesNotContain(events.OfType<ObjectMoved>(), move => move.To.Zone == library);
    }

    [Theory]
    [InlineData(Placement.Top, 0)]
    [InlineData(Placement.Bottom, 0)]
    [InlineData(Placement.Top, 1)]
    [InlineData(Placement.Bottom, 1)]
    [InlineData(Placement.Top, 2)]
    [InlineData(Placement.Bottom, 2)]
    public void Library_repositioning_preserves_identity_in_direct_and_resumed_batches(Placement placement, int path)
    {
        var table = new InterpreterTable();
        var engine = Subsystems.Wave2();
        var library = ZoneKey.Of(Zone.Library, table.A);
        for (var i = 0; i < 3; i++)
            table.Card(table.A, Zone.Library);
        var state = table.State;
        var original = state.In(library);
        var moved = original[1];
        var requests = new List<MoveRequest> { new(moved, library, placement) };
        if (path == 2)
        {
            var commander = new GameObject(new ObjectId(state.NextObjectId),
                InterpreterTable.Definition(CardType.Creature), 0, table.A, table.A,
                ZoneKey.Shared(Zone.Battlefield), state.NextTimestamp)
            { IsCommander = true };
            state = new ObjectCreated(commander, -1).Apply(state);
            requests.Insert(0, new MoveRequest(commander.Id, ZoneKey.Of(Zone.Hand, table.A)));
        }
        var before = state;
        var result = path == 0
            ? engine.Zones.Move(state, moved, library, placement)
            : engine.Zones.MoveAll(state, requests);
        var events = result.Events;
        state = GameReducer.Apply(before, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        if (path == 2)
        {
            var pending = Assert.IsType<PendingChoice>(state.Pending);
            Assert.Equal(original, state.In(library));
            Assert.Single(pending.Context.DeferredZoneMoves);
            result = engine.Choices.Answer(state, new AnswerChoice(table.A, pending.Id, ["keep"]));
            events = events.AddRange(result.Events);
            state = GameReducer.Apply(before, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        }
        Assert.Null(state.Pending);
        Assert.DoesNotContain(events.OfType<ObjectMoved>(), move => move.From == moved);
        var expected = original.Remove(moved).Insert(placement == Placement.Top ? 0 : 2, moved);
        Assert.Equal(expected, state.In(library));
        foreach (var id in original)
            Assert.Equal(before.Objects[id], state.Objects[id]);
    }
}
