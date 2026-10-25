using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

public sealed class LibraryResolutionShuffleTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Resolution_shuffles_only_after_the_library_arrival_replacement_finishes(int replacementCount)
    {
        var table = new InterpreterTable();
        for (var i = 0; i < 3; i++)
            table.Card(table.A, Zone.Library);
        for (var i = 0; i < replacementCount; i++)
            table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
                new ReplacementAbility(new WouldBePutIntoZone(ObjectFilter.Any with { In = Zone.Stack }, Zone.Library),
                    new ModifyAmount(Add: 0))));
        var spell = table.OnStack(table.A, new GainLife(new Fixed(0)));
        var definition = new CardDefinition("fixture-omen", "Fixture", Layout.Omen,
            [new Face("Front", FaceRole.Main, new TypeLine(CardType.Creature)),
                new Face("Omen", FaceRole.Omen, new TypeLine(CardType.Instant))]);
        var initial = table.State with
        {
            Objects = table.State.Objects.SetItem(spell, table.State.Objects[spell] with { Definition = definition, FaceIndex = 1 }),
        };
        var engine = Subsystems.Wave2();
        var result = engine.Casting.FinishResolving(initial, spell);
        var events = result.Events;
        var state = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        var choices = 0;
        while (state.Pending is { } pending && choices < 4)
        {
            Assert.Empty(events.OfType<ZoneReordered>());
            Assert.Equal(initial.Rng, state.Rng);
            Assert.Equal("replacement-order", pending.Kind);
            Assert.Single(pending.Context.DeferredShuffles);
            foreach (var viewer in new[] { table.A, table.B })
                Assert.Empty(engine.Views.Project(state, viewer).Pending!.Context.DeferredShuffles);
            result = engine.Choices.Answer(state, new AnswerChoice(pending.Player, pending.Id, [pending.Options[0].Id]));
            events = events.AddRange(result.Events);
            state = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
            choices++;
        }
        Assert.Null(state.Pending);
        Assert.Equal(replacementCount - 1, choices);
        var moved = Assert.Single(events.OfType<ObjectMoved>(), move => move.From == spell);
        var shuffled = Assert.Single(events.OfType<ZoneReordered>());
        Assert.True(shuffled.Shuffle);
        Assert.Contains(moved.To.Id, shuffled.Order);
        Assert.True(events.IndexOf(shuffled) > events.IndexOf(moved));
        Assert.Equal(4, state.In(ZoneKey.Of(Zone.Library, table.A)).Count);
    }
}
