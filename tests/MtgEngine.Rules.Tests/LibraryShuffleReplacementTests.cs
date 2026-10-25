using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

public sealed class LibraryShuffleReplacementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void Replacement_created_shuffle_does_not_reapply_its_source(bool search, int sources = 1)
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        Effect shuffle = search
            ? new Search(ObjectFilter.Any, Zone.Library, UpTo: new Fixed(0))
            : new Shuffle();
        // The life bound makes a missing recursion guard fail finitely rather than overflowing.
        for (var index = 0; index < sources; index++)
            table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
                new ReplacementAbility(new WouldShuffle(new You()),
                    new ReplaceWith(new Sequence([new GainLife(new Fixed(1)), shuffle])),
                    If: new Compare(new LifeOf(new You()), ComparisonOp.Lt, new Fixed(20 + sources * 2)))));
        var engine = Subsystems.Wave2();
        var result = engine.Zones.Shuffle(table.State, table.A);
        var events = result.Events;
        for (var attempts = 0; result.State.Pending is { } pending && attempts < 8; attempts++)
        {
            Assert.Contains(pending.Kind, new[] { "search", "replacement-order" });
            var restored = GameReducer.Apply(table.State, EventLogSerializer.Read(EventLogSerializer.Write(events)));
            result = engine.Choices.Answer(restored, new AnswerChoice(pending.Player, pending.Id,
                pending.Kind == "search" ? [] : [pending.Options[0].Id]));
            events = events.AddRange(result.Events);
        }
        Assert.Null(result.State.Pending);
        Assert.Equal(20 + sources, result.State.Player(table.A).Life);
        Assert.Single(events.OfType<ZoneReordered>(), item => item.Shuffle);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Replaced_shuffle_does_not_advance_randomness_or_reorder(int count)
    {
        var table = new InterpreterTable();
        for (var index = 0; index < count; index++)
            table.Card(table.A, Zone.Library);
        table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new ReplacementAbility(new WouldShuffle(new You()), new ReplaceWith(new GainLife(new Fixed(1))))));
        var initial = table.State;
        var result = Subsystems.Wave2().Zones.Shuffle(initial, table.A);
        Assert.Null(result.State.Pending);
        Assert.Equal(initial.Rng, result.State.Rng);
        Assert.Equal(initial.In(ZoneKey.Of(Zone.Library, table.A)), result.State.In(ZoneKey.Of(Zone.Library, table.A)));
        Assert.Equal(initial.Player(table.A).Life + 1, result.State.Player(table.A).Life);
        Assert.Empty(result.Events.OfType<RngAdvanced>());
        Assert.Empty(result.Events.OfType<ZoneReordered>());
    }

    [Fact]
    public void Shuffle_effect_waits_for_replacements_before_following_instructions()
    {
        var table = new InterpreterTable();
        for (var index = 0; index < 3; index++)
            table.Card(table.A, Zone.Library);
        for (var index = 0; index < 2; index++)
            table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
                new ReplacementAbility(new WouldShuffle(new You()), new ModifyAmount(Add: 0))));
        var spell = table.OnStack(table.A, new Sequence([new Shuffle(), new GainLife(new Fixed(2))]));
        var engine = Subsystems.Wave2();
        var initial = table.State;
        var started = engine.Interpreter.Resolve(initial, spell);
        var pending = Assert.IsType<PendingChoice>(started.State.Pending);
        Assert.Equal(initial.Rng, started.State.Rng);
        Assert.Equal(initial.Player(table.A).Life, started.State.Player(table.A).Life);
        foreach (var viewer in new[] { table.A, table.B })
            Assert.Null(engine.Views.Project(started.State, viewer).Pending!.Context.Event);
        var restored = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(started.Events)));
        var result = engine.Choices.Answer(restored, new AnswerChoice(pending.Player, pending.Id, [pending.Options[0].Id]));
        restored = GameReducer.Apply(restored, EventLogSerializer.Read(EventLogSerializer.Write(result.Events)));
        Assert.Null(restored.Pending);
        Assert.Equal(initial.Player(table.A).Life + 2, restored.Player(table.A).Life);
        Assert.Single(result.Events.OfType<ZoneReordered>(), item => item.Shuffle && !item.AwaitingRandomization);
        Assert.Single(result.Events.OfType<RngAdvanced>());
        Assert.Equal(result.State.Rng, restored.Rng);
    }
}
