using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;

namespace MtgEngine.Rules.Tests;

public sealed class TableViewTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_viewer_is_at_bottom_and_opponents_keep_circular_order(int count)
    {
        var state = GameReducer.Replay([new GameStarted(
            [.. Enumerable.Range(0, count).Select(i => new PlayerStart($"Seat {i}", [], []))],
            Seed: 42, StartingLife: 40, Format: "test")]);
        var projector = new ViewProjector(new LayerSystem(), _ => true);
        for (var viewer = 0; viewer < count; viewer++)
        {
            var view = projector.Project(state, new PlayerId(viewer));
            var table = Assert.IsType<TableView>(view.Table);
            Assert.Equal(viewer, table.Self.Seat);
            Assert.Equal(new PlayerId(viewer), table.Self.Player);
            Assert.Equal(Enumerable.Range(1, count - 1).Select(i => (viewer + i) % count),
                table.Opponents.Select(p => p.Seat));
            Assert.Equal(Enumerable.Range(0, count).Select(i => new PlayerId(i)), view.Players.Select(p => p.Id));
        }
    }

    [Fact]
    public void Battlefield_groups_by_controller_but_personal_exile_by_owner()
    {
        var view = EmptyView() with
        {
            Battlefield = [Object(1, 0, 1, "Creature"), Object(2, 0, 0, "Land"), Object(3, 0, 0, "Artifact")],
            Exile = [Object(4, 0, 1, "Creature")],
        };
        var table = TableView.From(view);
        Assert.Equal([new ObjectId(1)], table.Opponents[0].Battlefield.Creatures);
        Assert.Empty(table.Self.Battlefield.Creatures);
        Assert.Equal([new ObjectId(2)], table.Self.Battlefield.Lands);
        Assert.Equal([new ObjectId(3)], table.Self.Battlefield.OtherPermanents);
        Assert.Equal([new ObjectId(4)], table.Self.OwnedExile);
        Assert.Empty(table.Opponents[0].OwnedExile);
    }

    [Fact]
    public void Every_permanent_occurs_once_and_land_creatures_use_the_creature_row()
    {
        var view = EmptyView() with
        {
            Battlefield = [Object(1, 0, 0, "Land", "Creature"), Object(2, 0, 0, "Artifact", "Land"), Object(3, 0, 0)],
        };
        var rows = TableView.From(view).Self.Battlefield;
        Assert.Equal([new ObjectId(1)], rows.Creatures);
        Assert.Equal([new ObjectId(2)], rows.Lands);
        Assert.Equal([new ObjectId(3)], rows.OtherPermanents);
        Assert.Equal(view.Battlefield.Select(o => o.Id).OrderBy(id => id.Value),
            rows.Creatures.Concat(rows.Lands).Concat(rows.OtherPermanents).OrderBy(id => id.Value));
    }

    [Fact]
    public void Stack_is_top_first_without_mutating_the_authoritative_order()
    {
        var view = EmptyView() with { Stack = [Object(8, 0, 0), Object(9, 1, 1)] };
        Assert.Equal([new ObjectId(9), new ObjectId(8)], TableView.From(view).StackTopFirst);
        Assert.Equal([new ObjectId(8), new ObjectId(9)], view.Stack.Select(o => o.Id));
    }

    [Fact]
    public void Private_zones_are_not_sources_of_board_references()
    {
        var view = EmptyView() with { Hand = [Object(99, 0, 0, "Creature")] };
        var table = TableView.From(view);
        Assert.Empty(table.Self.Battlefield.Creatures);
        Assert.Empty(table.Self.OwnedExile);
        Assert.Empty(table.StackTopFirst);
        Assert.All(table.Opponents, p => Assert.Empty(p.Battlefield.Creatures));
    }

    [Fact]
    public void Invalid_topologies_are_rejected()
    {
        var view = EmptyView();
        Assert.Throws<ArgumentException>(() => TableView.From(view with { Viewer = new PlayerId(99) }));
        Assert.Throws<ArgumentException>(() => TableView.From(view with { Players = [view.Players[0]] }));
        Assert.Throws<ArgumentException>(() => TableView.From(view with { Players = [view.Players[0], view.Players[0]] }));
    }

    private static GameView EmptyView()
    {
        var state = GameReducer.Replay([new GameStarted(
            [new PlayerStart("A", [], []), new PlayerStart("B", [], [])], Seed: 42, StartingLife: 40, Format: "test")]);
        return new ViewProjector(new LayerSystem(), _ => true).Project(state, new PlayerId(0));
    }

    private static ObjectView Object(int id, int owner, int controller, params string[] types) =>
        new(new ObjectId(id), "Fixture", "fixture", new PlayerId(controller), new PlayerId(owner),
            false, false, null, null, null, 0, ImmutableDictionary<string, int>.Empty,
            null, null, [], [.. types], [], true);
}
