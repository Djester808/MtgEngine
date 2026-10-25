using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

public sealed class LibraryNestedMovementTests
{

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void Deferred_library_moves_offer_both_replacements_for_each_card_after_reload(int selected, bool inherited = false)
    {
        var table = new InterpreterTable();
        var cards = Enumerable.Range(0, 3).Select(_ => table.Card(table.A, Zone.Library)).ToArray();
        var used = inherited ? table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new ReplacementAbility(new WouldBePutIntoZone(ObjectFilter.Any with { In = Zone.Library }, Zone.Hand),
                new ReplaceWith(new GainLife(new Fixed(9)))))) : new ObjectId(0);
        for (var index = 0; index < 2; index++)
            table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
                new ReplacementAbility(new WouldBePutIntoZone(ObjectFilter.Any with { In = Zone.Library }, Zone.Hand),
                    new ModifyAmount(Add: 0))));
        var result = Subsystems.Wave2().Zones.MoveAll(table.State,
            cards.Select(card => new MoveRequest(card, ZoneKey.Of(Zone.Hand, table.A))).ToArray(),
            inherited ? [$"o{used.Value}:a0"] : null);
        var events = result.Events;
        for (var index = 0; index < cards.Length; index++)
        {
            var restored = GameReducer.Apply(table.State, EventLogSerializer.Read(EventLogSerializer.Write(events)));
            var pending = Assert.IsType<PendingChoice>(restored.Pending);
            Assert.Equal("replacement-order", pending.Kind);
            Assert.Equal(2, pending.Options.Count);
            Assert.Equal(cards.Length - index - 1, pending.Context.DeferredZoneMoves.Count);
            var proposed = Assert.IsType<ObjectMoved>(pending.Context.Event);
            Assert.Equal(cards[index], proposed.From);
            var engine = Subsystems.Wave2();
            foreach (var viewer in new[] { table.A, table.B })
            {
                Assert.Empty(engine.Views.Project(restored, viewer).Pending!.Context.DeferredZoneMoves);
                Assert.Empty(engine.Views.Project(restored, viewer).Pending!.Context.DeferredMoveHistory);
            }
            result = engine.Choices.Answer(restored,
                new AnswerChoice(table.A, pending.Id, [pending.Options[selected].Id]));
            events = events.AddRange(result.Events);
        }
        var final = GameReducer.Apply(table.State, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        Assert.Null(final.Pending);
        Assert.Empty(final.In(ZoneKey.Of(Zone.Library, table.A)));
        Assert.Equal(cards.Length, final.In(ZoneKey.Of(Zone.Hand, table.A)).Count);
        Assert.Equal(cards, events.OfType<ObjectMoved>().Select(move => move.From));
        Assert.Equal(cards.Length, events.OfType<ObjectMoved>().Select(move => move.To.Id).Distinct().Count());
    }

    [Fact]
    public void Replacement_search_preserves_used_movement_history_across_reload()
    {
        var table = new InterpreterTable();
        var card = table.Card(table.A, Zone.Library);
        table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new ReplacementAbility(new WouldBePutIntoZone(ObjectFilter.Any with { In = Zone.Library }, Zone.Hand),
                new ReplaceWith(new Sequence([new GainLife(new Fixed(1)),
                    new Search(ObjectFilter.Any, Zone.Hand, UpTo: new Fixed(1), Reveal: false, ShuffleAfter: false)])),
                If: new Compare(new LifeOf(new You()), ComparisonOp.Lt, new Fixed(23)))));
        var engine = Subsystems.Wave2();
        var result = engine.Zones.Move(table.State, card, ZoneKey.Of(Zone.Hand, table.A));
        var events = result.Events;
        var answers = 0;
        while (result.State.Pending is { } pending && answers++ < 4)
        {
            Assert.Equal("search", pending.Kind);
            var restored = GameReducer.Apply(table.State, EventLogSerializer.Read(EventLogSerializer.Write(events)));
            result = Subsystems.Wave2().Choices.Answer(restored,
                new AnswerChoice(table.A, pending.Id, [pending.Options[0].Id]));
            events = events.AddRange(result.Events);
        }
        Assert.Null(result.State.Pending);
        Assert.Equal(1, answers);
        Assert.Single(events.OfType<LifeChanged>());
        Assert.Single(result.State.In(ZoneKey.Of(Zone.Hand, table.A)));
        Assert.Empty(result.State.In(ZoneKey.Of(Zone.Library, table.A)));
        Assert.Empty(result.State.LibraryInspections);
    }
}
