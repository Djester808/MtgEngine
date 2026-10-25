using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

public sealed class LibraryNestedDrawTests
{
    [Fact]
    public void Deferred_draw_batch_preserves_inherited_replacement_history()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Library);
        var used = table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new ReplacementAbility(new WouldDraw(new You()), new ReplaceWith(new GainLife(new Fixed(9))))));
        for (var index = 0; index < 2; index++)
            table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
                new ReplacementAbility(new WouldDraw(new You()), new ModifyAmount(Add: 0))));
        var engine = Subsystems.Wave2();
        var result = engine.Zones.Draw(table.State, table.A, 2, [$"o{used.Value}:a0"]);
        var events = result.Events;
        var questions = 0;
        while (result.State.Pending is { } pending && questions < 4)
        {
            questions++;
            Assert.Equal(2, pending.Options.Count);
            foreach (var viewer in new[] { table.A, table.B })
                Assert.Empty(engine.Views.Project(result.State, viewer).Pending!.Context.DeferredDraws);
            var restored = GameReducer.Apply(table.State, EventLogSerializer.Read(EventLogSerializer.Write(events)));
            result = engine.Choices.Answer(restored, new AnswerChoice(pending.Player, pending.Id, [pending.Options[0].Id]));
            events = events.AddRange(result.Events);
        }
        Assert.Equal(2, questions);
        Assert.Null(result.State.Pending);
        Assert.Equal(20, result.State.Player(table.A).Life);
        Assert.Equal(2, result.State.In(ZoneKey.Of(Zone.Hand, table.A)).Count);
        Assert.Equal(2, events.OfType<ObjectDrawn>().Count());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void Replacement_created_draw_preserves_used_effects_across_reload(int sources, bool empty)
    {
        var table = new InterpreterTable();
        if (!empty)
            table.Card(table.A, Zone.Library);
        for (var index = 0; index < sources; index++)
            table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
                new ReplacementAbility(new WouldDraw(new You()), new ReplaceWith(new Sequence([
                    new GainLife(new Fixed(1)), new May(new GainLife(new Fixed(1))), new Draw(new Fixed(1)),
                ])), If: new Compare(new LifeOf(new You()), ComparisonOp.Lt, new Fixed(20 + sources * 2)))));
        var engine = Subsystems.Wave2();
        var result = engine.Zones.Draw(table.State, table.A, 1);
        var events = result.Events;
        for (var attempt = 0; result.State.Pending is { } pending && attempt < 12; attempt++)
        {
            var restored = GameReducer.Apply(table.State, EventLogSerializer.Read(EventLogSerializer.Write(events)));
            result = engine.Choices.Answer(restored, new AnswerChoice(pending.Player, pending.Id,
                [pending.Kind == "may" ? "no" : pending.Options[0].Id]));
            events = events.AddRange(result.Events);
        }
        Assert.Null(result.State.Pending);
        Assert.Equal(20 + sources, result.State.Player(table.A).Life);
        Assert.Equal(empty ? 0 : 1, result.State.In(ZoneKey.Of(Zone.Hand, table.A)).Count);
        Assert.Equal(empty, result.State.Player(table.A).DrewFromEmptyLibrary);
        Assert.Equal(empty ? 0 : 1, events.OfType<ObjectDrawn>().Count());
        Assert.Equal(empty ? 1 : 0, events.OfType<DrawFromEmptyLibraryAttempted>().Count());
    }
}
