using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

public sealed partial class InterpreterTests
{
    [Fact]
    public void Surveilling_a_revealed_top_card_moves_that_card_after_reordering_the_remainder()
    {
        var table = new InterpreterTable(new MtgEngine.Rules.Layers.LayerSystem());
        table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new StaticAbility(new PlayWithTopCardRevealed(new You()))));
        table.Put(table.A, ZoneKey.Of(Zone.Library, table.A),
            InterpreterTable.Definition(CardType.Creature) with { OracleId = "first" });
        table.Put(table.A, ZoneKey.Of(Zone.Library, table.A),
            InterpreterTable.Definition(CardType.Land) with { OracleId = "second" });
        var spell = table.OnStack(table.A, new Surveil(new Fixed(2)));
        var top = table.State.In(ZoneKey.Of(Zone.Library, table.A))[0];
        var expectedOracle = table.State.Objects[top].Definition.OracleId;
        var initial = table.State;
        var events = table.Resolve(spell);
        var selected = table.Pending.Options.Single(option => option.Object == top);
        events = events.AddRange(table.Answer(selected.Id));
        Assert.Null(table.State.Pending);
        var moved = Assert.Single(table.In(Zone.Graveyard, table.A));
        Assert.Equal(expectedOracle, table.State.Objects[moved].Definition.OracleId);
        Assert.Single(table.In(Zone.Library, table.A));
        var replayed = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        Assert.Single(replayed.In(ZoneKey.Of(Zone.Graveyard, table.A)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void One_effect_hiding_and_revealing_the_top_card_resets_its_identity(bool pause)
    {
        var table = new InterpreterTable(new MtgEngine.Rules.Layers.LayerSystem());
        table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new StaticAbility(new PlayWithTopCardRevealed(new You()))));
        var top = table.Card(table.A, Zone.Library);
        var source = new Each(ObjectFilter.Any with { In = Zone.Battlefield });
        Effect effect = pause
            ? new Sequence([new TurnFaceDown(source), new May(new GainLife(new Fixed(1))), new TurnFaceUp(source)])
            : new Sequence([new TurnFaceDown(source), new TurnFaceUp(source)]);
        var spell = table.OnStack(table.A, effect);
        var initial = table.State;
        var events = table.Resolve(spell);
        if (pause)
        {
            Assert.Equal("may", table.Pending.Kind);
            Assert.DoesNotContain(table.A, table.State.RevealedLibraryTops.Keys);
            events = events.AddRange(table.Answer("no"));
        }
        var current = Assert.Single(table.In(Zone.Library, table.A));
        Assert.NotEqual(top, current);
        Assert.Single(events.OfType<MtgEngine.Rules.Library.LibraryCardReidentified>());
        var replayed = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        Assert.Equal(current, Assert.Single(replayed.In(ZoneKey.Of(Zone.Library, table.A))));
    }

    [Theory]
    [InlineData(Placement.Top, true)]
    [InlineData(Placement.Bottom, true)]
    [InlineData(Placement.Bottom, false)]
    public void Searching_a_revealed_card_to_library_preserves_the_search_result_across_shuffle(Placement placement, bool shuffle)
    {
        var table = new InterpreterTable(new MtgEngine.Rules.Layers.LayerSystem());
        table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new StaticAbility(new PlayWithTopCardRevealed(new You()))));
        table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Library);
        var spell = table.OnStack(table.A, new Sequence([
            new Search(ObjectFilter.Any, Zone.Library, Where: placement, RememberAs: "found", ShuffleAfter: shuffle),
            new May(new GainLife(new Fixed(1))),
            new Exile(new Remembered("found")),
        ]));
        var top = table.State.In(ZoneKey.Of(Zone.Library, table.A))[0];
        var state = table.State with
        {
            RevealedLibraryTops = ImmutableDictionary<PlayerId, ObjectId>.Empty.Add(table.A, top),
        };
        var started = table.Subject.Resolve(state, spell);
        var pending = started.State.Pending!;
        var option = pending.Options.Single(item => item.Object == top);
        var answered = new ChoiceAnswered(pending.Id, pending.Player, [option.Id]).Apply(started.State);
        var result = table.Subject.Resume(answered,
            new Resumption(pending.Kind, pending.Player, [option], pending.Context));
        var library = result.State.In(ZoneKey.Of(Zone.Library, table.A));
        var replacement = Assert.Single(result.State.Objects.Keys.Except(state.Objects.Keys));
        Assert.Equal(replacement, placement == Placement.Top ? library[0] : library[^1]);
        var restored = GameReducer.Apply(answered, EventLogSerializer.Read(EventLogSerializer.Write(result.Events)));
        var next = restored.Pending!;
        Assert.Equal("may", next.Kind);
        var no = next.Options.Single(item => item.Id == "no");
        result = table.Subject.Resume(new ChoiceAnswered(next.Id, next.Player, [no.Id]).Apply(restored),
            new Resumption(next.Kind, next.Player, [no], next.Context));
        Assert.Single(result.State.In(ZoneKey.Shared(Zone.Exile)));
        Assert.Single(result.State.In(ZoneKey.Of(Zone.Library, table.A)));
    }
}
