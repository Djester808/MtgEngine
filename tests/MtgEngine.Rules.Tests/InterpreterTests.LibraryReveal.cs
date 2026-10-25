using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;

namespace MtgEngine.Rules.Tests;

public sealed partial class InterpreterTests
{
    [Theory]
    [InlineData(Zone.Library, true)]
    [InlineData(Zone.Hand, false)]
    public void Reveal_records_library_origin_without_linking_the_public_snapshot_to_an_object(Zone zone, bool fromLibrary)
    {
        var table = new InterpreterTable(new LayerSystem());
        table.Card(table.A, zone);
        var events = table.Resolve(table.OnStack(table.A, new Reveal(new Top(new You(), new Fixed(1), zone))));
        var replayEvents = EventLogSerializer.Read(EventLogSerializer.Write(events));
        Assert.Equal(fromLibrary, Assert.Single(replayEvents.OfType<CardsRevealed>()).FromLibrary);
        Assert.DoesNotContain(typeof(RevealedCard).GetProperties(), property => property.Name is "Id" or "ObjectId" or "Zone");
    }

    [Fact]
    public void Looking_at_library_cards_persists_only_the_authorized_players_historical_disclosure()
    {
        var table = new InterpreterTable(new LayerSystem());
        var (land, _) = Stocked(table);
        var spell = table.OnStack(table.A, new Sequence([
            new LookAt(new Top(new You(), new Fixed(1))), new Shuffle(),
        ]));
        var before = table.State;
        var events = table.Resolve(spell);
        var restored = GameReducer.Apply(before, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        var projector = new ViewProjector(new LayerSystem(), _ => true);
        var mine = projector.Project(restored, table.A);
        var theirs = projector.Project(restored, table.B);
        Assert.NotNull(mine.LastLook);
        Assert.Equal(before.Objects[land].Definition.OracleId, Assert.Single(mine.LastLook.Cards).OracleId);
        Assert.Null(theirs.LastLook);
        Assert.Null(mine.LastReveal);
        Assert.Null(theirs.LastReveal);
        Assert.Single(events.OfType<CardsLookedAt>());
        Assert.DoesNotContain(events, e => e is ObjectMoved or CardsRevealed);
        Assert.Equal(2, restored.In(ZoneKey.Of(Zone.Library, table.A)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Search_reveals_only_the_selected_card_before_moving_it_when_required(bool reveal)
    {
        var table = new InterpreterTable(new LayerSystem());
        var (land, creature) = Stocked(table);
        table.Resolve(table.OnStack(table.A, new Search(ObjectFilter.Any, Zone.Hand, Reveal: reveal)));
        var before = table.State;
        var events = table.Answer(Option(table, land)).ToList();
        var restored = GameReducer.Apply(before, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        var view = new ViewProjector(new LayerSystem(), _ => true).Project(restored, table.B);
        Assert.Empty(view.Hand);
        Assert.Contains(creature, restored.In(ZoneKey.Of(Zone.Library, table.A)));
        if (!reveal)
        {
            Assert.Null(view.LastReveal);
            Assert.DoesNotContain(events, e => e is CardsRevealed);
            return;
        }
        Assert.NotNull(view.LastReveal);
        Assert.Single(view.LastReveal.Cards);
        Assert.Equal(before.Objects[land].Definition.OracleId, view.LastReveal.Cards[0].OracleId);
        Assert.InRange(events.FindIndex(e => e is CardsRevealed), 0, events.FindIndex(e => e is ObjectMoved) - 1);
        Assert.DoesNotContain(typeof(RevealedCard).GetProperties(), p => p.Name is "Id" or "ObjectId" or "CardInstanceId" or "Zone");
    }
}
