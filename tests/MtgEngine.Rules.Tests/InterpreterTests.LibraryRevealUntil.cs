using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;

namespace MtgEngine.Rules.Tests;

public sealed partial class InterpreterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reveal_until_scans_the_named_players_libraries_and_remembers_each_hit(bool eachPlayer)
    {
        var table = new InterpreterTable(new LayerSystem());
        foreach (var owner in new[] { table.A, table.B })
        {
            table.Put(owner, ZoneKey.Of(Zone.Library, owner), InterpreterTable.Definition(CardType.Land));
            table.Card(owner, Zone.Library);
            table.Card(owner, Zone.Library);
        }
        var events = table.Resolve(table.OnStack(table.A, new Sequence([
            new RevealUntil(eachPlayer ? new EachPlayer() : new TargetPlayer(PlayerFilter.Any), ObjectFilter.Creature, "hit", "rest"),
            new Move(new Remembered("hit"), Zone.Hand),
        ]), new StackEntry(null, null, [], [table.B], [], null, false)));
        Assert.Equal(eachPlayer ? 4 : 2, Assert.Single(events.OfType<CardsRevealed>()).Reveal.Cards.Count);
        Assert.Equal(eachPlayer ? 1 : 0, table.In(Zone.Hand, table.A).Count);
        Assert.Single(table.In(Zone.Hand, table.B));
        Assert.Equal(eachPlayer ? 2 : 3, table.In(Zone.Library, table.A).Count);
        Assert.Equal(2, table.In(Zone.Library, table.B).Count);
    }

    [Theory]
    [InlineData(null, 2, 1)]
    [InlineData(0, 0, 0)]
    [InlineData(-1, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(2, 2, 1)]
    [InlineData(20, 2, 1)]
    public void Reveal_until_stops_at_the_first_match_or_limit_without_drawing_or_reordering(int? limit, int shown, int hits)
    {
        var table = new InterpreterTable(new LayerSystem());
        var land = table.Put(table.A, ZoneKey.Of(Zone.Library, table.A), InterpreterTable.Definition(CardType.Land) with { OracleId = "land" });
        var creature = table.Put(table.A, ZoneKey.Of(Zone.Library, table.A), InterpreterTable.Definition(CardType.Creature) with { OracleId = "creature" });
        var unseen = table.Card(table.A, Zone.Library);
        var spell = table.OnStack(table.A, new Sequence([
            new RevealUntil(new You(), ObjectFilter.Creature, "hit", "rest", limit is { } n ? new Fixed(n) : null),
            new May(new GainLife(new Fixed(1))),
            new Move(new Remembered("hit"), Zone.Hand), new Move(new Remembered("rest"), Zone.Graveyard),
        ]));
        var before = table.State;
        var events = table.Resolve(spell);
        Assert.Equal(new[] { land, creature, unseen }, table.In(Zone.Library, table.A));
        Assert.DoesNotContain(events, e => e is ObjectMoved or ObjectDrawn or ZoneReordered);
        var restored = GameReducer.Apply(before, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        foreach (var player in new[] { table.A, table.B })
        {
            var view = new ViewProjector(new LayerSystem(), _ => true).Project(restored, player);
            Assert.Equal(shown, view.LastReveal?.Cards.Count ?? 0);
            if (shown > 0)
                Assert.Equal("land", view.LastReveal!.Cards[0].OracleId);
            if (hits > 0)
                Assert.Equal("creature", view.LastReveal!.Cards[^1].OracleId);
        }
        // Resume from the serialized pending instruction, retaining both remembered sets.
        var pending = restored.Pending!;
        var completed = table.Subject.Resume(new ChoiceAnswered(pending.Id, pending.Player, ["no"]).Apply(restored),
            new Resumption(pending.Kind, pending.Player, [pending.Options.Single(option => option.Id == "no")], pending.Context));
        Assert.Equal(hits, completed.State.In(ZoneKey.Of(Zone.Hand, table.A)).Count);
        Assert.Equal(shown - hits, completed.State.In(ZoneKey.Of(Zone.Graveyard, table.A)).Count);
        Assert.Contains(unseen, completed.State.In(ZoneKey.Of(Zone.Library, table.A)));
        Assert.DoesNotContain(completed.Events, e => e is CardsRevealed or ObjectDrawn);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Reveal_until_with_no_match_exhausts_the_available_cards_without_an_empty_library_draw(int cards)
    {
        var table = new InterpreterTable(new LayerSystem());
        for (var i = 0; i < cards; i++)
            table.Put(table.A, ZoneKey.Of(Zone.Library, table.A), InterpreterTable.Definition(CardType.Land));
        var events = table.Resolve(table.OnStack(table.A, new Sequence([
            new RevealUntil(new You(), ObjectFilter.Creature, "hit", "rest"),
            new Move(new Remembered("hit"), Zone.Hand), new Move(new Remembered("rest"), Zone.Graveyard),
        ])));
        Assert.Empty(table.In(Zone.Hand, table.A));
        Assert.Empty(table.In(Zone.Library, table.A));
        Assert.Equal(cards, table.In(Zone.Graveyard, table.A).Count);
        Assert.Equal(cards, table.State.LastReveal?.Cards.Count ?? 0);
        Assert.False(table.State.Player(table.A).DrewFromEmptyLibrary);
        Assert.DoesNotContain(events, e => e is ObjectDrawn);
    }
}
