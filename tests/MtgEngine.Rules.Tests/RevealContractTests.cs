using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "Reveal")]
public sealed class RevealContractTests
{
    [Fact]
    public void Public_snapshots_reach_four_seats_but_looks_reach_only_named_viewers()
    {
        var state = GameReducer.Replay([new GameStarted([
            new PlayerStart("one", [], []), new PlayerStart("two", [], []),
            new PlayerStart("three", [], []), new PlayerStart("four", [], [])], 7, 20, "commander")]);
        var publicCard = new RevealedCard("public", "Public", null, null, "public-art");
        var privateCard = new RevealedCard("private", "Private", null, null, "private-art");
        state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write([
            new CardsRevealed(new CardReveal(new PlayerId(0), [publicCard])),
            new CardsLookedAt([new PlayerId(1), new PlayerId(3)], [privateCard])])));
        foreach (var seat in state.Players)
        {
            var view = Subsystems.Wave2().Views.Project(state, seat.Id);
            Assert.Equal(publicCard, Assert.Single(view.LastReveal!.Cards));
            if (seat.Id.Value is 1 or 3)
                Assert.Equal(privateCard, Assert.Single(view.LastLook!.Cards));
            else
                Assert.Null(view.LastLook);
            Assert.Empty(VisibleGameObjects.Current(view));
        }
    }

    [Fact]
    public void Empty_area_has_no_snapshots_or_live_references()
    {
        var table = new InterpreterTable();
        var area = new RevealArea(table.State, table.A);
        Assert.Equal(0, area.Count);
        Assert.Null(area.PublicReveal);
        Assert.Null(area.PrivateLook);
        Assert.Empty(area.Inspections);
        Assert.False(area.Contains(new ObjectId(999)));
    }

    [Fact]
    public void Overlapping_scopes_deduplicate_live_access_and_preserve_provenance()
    {
        var table = new InterpreterTable();
        var card = table.Card(table.A, Zone.Library);
        var state = new LibraryInspectionChanged("public", new LibraryInspection(table.B, table.A, [card])
        { Resolution = "resolve", PublicReveal = true }).Apply(table.State);
        state = new LibraryInspectionChanged("private", new LibraryInspection(table.B, table.A, [card])).Apply(state);
        var area = new RevealArea(state, table.B);
        Assert.Equal(1, area.Count);
        Assert.True(area.Contains(card));
        Assert.True(area.Inspections["public"].PublicReveal);
        Assert.False(area.Inspections["private"].PublicReveal);
        Assert.Equal("resolve", area.Inspections["public"].Resolution);
        Assert.Equal(0, new RevealArea(state, table.A).Count);
        Assert.Equal(table.State.Zones, state.Zones);
        Assert.Equal(table.State.Objects, state.Objects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Expiry_or_shuffle_removes_live_access_but_retains_disclosed_history(bool shuffle)
    {
        var table = new InterpreterTable();
        var card = table.Card(table.A, Zone.Library);
        var snapshot = new RevealedCard("known", "Known", null, "Descriptive", "printing");
        var events = ImmutableList.Create<GameEvent>(
            new CardsRevealed(new CardReveal(table.A, [snapshot])),
            new CardsLookedAt([table.B], [snapshot]),
            new LibraryInspectionChanged("look", new LibraryInspection(table.B, table.A, [card])));
        var state = GameReducer.Apply(table.State, events);
        var captured = new RevealArea(state, table.B);
        GameEvent end = shuffle ? new ZoneReordered(ZoneKey.Of(Zone.Library, table.A), [card], true)
            : new LibraryInspectionChanged("look", null);
        state = GameReducer.Apply(table.State, EventLogSerializer.Read(EventLogSerializer.Write(events.Add(end))));
        var area = new RevealArea(state, table.B);
        Assert.Equal(0, area.Count);
        Assert.Equal(1, captured.Count);
        Assert.Equal(snapshot, Assert.Single(area.PublicReveal!.Cards));
        Assert.Equal("known/printing", Assert.Single(area.PrivateLook!.Cards).ArtworkKey);
        Assert.Null(new RevealArea(state, table.A).PrivateLook);
        Assert.Empty(VisibleGameObjects.Current(Subsystems.Wave2().Views.Project(state, table.B)));
    }

    [Fact]
    public void Departure_and_return_do_not_revive_a_stale_inspection()
    {
        var table = new InterpreterTable();
        var card = table.Card(table.A, Zone.Library);
        var state = new LibraryInspectionChanged("look", new LibraryInspection(table.B, table.A, [card])).Apply(table.State);
        var zones = Subsystems.Wave2().Zones;
        var moved = zones.Move(state, card, ZoneKey.Of(Zone.Hand, table.A));
        Assert.Equal(0, new RevealArea(moved.State, table.B).Count);
        var hand = Assert.Single(moved.State.In(ZoneKey.Of(Zone.Hand, table.A)));
        var returned = zones.Move(moved.State, hand, ZoneKey.Of(Zone.Library, table.A));
        Assert.Equal(0, new RevealArea(returned.State, table.B).Count);
        Assert.NotEqual(card, Assert.Single(returned.State.In(ZoneKey.Of(Zone.Library, table.A))));
    }

    [Fact]
    public void Reordering_a_public_top_invalidates_its_old_reference_without_granting_a_new_one()
    {
        var table = new InterpreterTable();
        var first = table.Card(table.A, Zone.Library);
        var second = table.Card(table.A, Zone.Library);
        var state = new LibraryInspectionChanged("resolution", new LibraryInspection(table.B, table.A, [first])
        { Resolution = "resolution", PublicReveal = true }).Apply(table.State);
        state = state with { RevealedLibraryTops = state.RevealedLibraryTops.Add(table.A, first) };
        var changed = new ZoneReordered(ZoneKey.Of(Zone.Library, table.A), [second, first]);
        var restored = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write([changed])));
        var current = restored.In(ZoneKey.Of(Zone.Library, table.A))[1];
        Assert.NotEqual(first, current);
        Assert.Equal(first, Assert.Single(restored.LibraryInspections["resolution"].Cards));
        Assert.Empty(Subsystems.Wave2().Views.Project(restored, table.B).InspectedLibraryCards);
        Assert.Empty(Subsystems.Wave2().Views.Project(restored, table.A).InspectedLibraryCards);
        Assert.Equal(state.Objects[first].CardInstanceId, restored.Objects[current].CardInstanceId);
    }

    [Fact]
    public void A_stale_inspection_cannot_disclose_an_object_absent_from_authoritative_membership()
    {
        var table = new InterpreterTable();
        var card = table.Card(table.A, Zone.Library);
        var state = new LibraryInspectionChanged("look", new LibraryInspection(table.B, table.A, [card])).Apply(table.State);
        state = state with { Zones = state.Zones.SetItem(ZoneKey.Of(Zone.Library, table.A), []) };
        // The previous projector accepted this dangling reference solely from the object's zone field.
        Assert.Equal(ZoneKey.Of(Zone.Library, table.A), state.Objects[card].Zone);
        Assert.Empty(Subsystems.Wave2().Views.Project(state, table.B).InspectedLibraryCards);
    }
}

