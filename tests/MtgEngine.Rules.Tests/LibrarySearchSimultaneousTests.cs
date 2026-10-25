using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

public sealed class LibrarySearchSimultaneousTests
{
    [Theory]
    [InlineData(Placement.Top)]
    [InlineData(Placement.Bottom)]
    public void Reordering_shared_search_results_keeps_each_physical_card_once(Placement placement)
    {
        var table = new InterpreterTable();
        var first = table.Card(table.A, Zone.Library);
        var second = table.Card(table.A, Zone.Library);
        var spell = table.OnStack(table.A, new Sequence([
            new Search(ObjectFilter.Any, Zone.Library, Who: new EachPlayer(), LibraryOf: new You(),
                ShuffleAfter: false, RememberAs: "found"),
            new Reorder(new Remembered("found"), placement),
        ]));
        var engine = Subsystems.Wave2();
        var result = engine.Interpreter.Resolve(table.State, spell);
        var events = result.Events;
        for (var index = 0; index < 2; index++)
        {
            var state = GameReducer.Apply(table.State, EventLogSerializer.Read(EventLogSerializer.Write(events)));
            var pending = Assert.IsType<PendingChoice>(state.Pending);
            result = engine.Choices.Answer(state, new AnswerChoice(pending.Player, pending.Id,
                [pending.Options.Single(option => option.Object == first).Id]));
            events = events.AddRange(result.Events);
        }
        var final = GameReducer.Apply(table.State, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        Assert.Null(final.Pending);
        var library = final.In(ZoneKey.Of(Zone.Library, table.A));
        Assert.Equal(2, library.Count);
        Assert.Equal(2, library.Distinct().Count());
        Assert.Equal(placement == Placement.Top ? new[] { first, second } : new[] { second, first }, library);
    }

    [Theory]
    [InlineData(Placement.Top)]
    [InlineData(Placement.Bottom)]
    [InlineData(Placement.Top, true)]
    [InlineData(Placement.Bottom, true)]
    [InlineData(Placement.Top, false, true)]
    [InlineData(Placement.Bottom, false, true)]
    [InlineData(Placement.Top, true, true)]
    [InlineData(Placement.Bottom, true, true)]
    public void Shared_library_search_results_follow_revealed_identity_changes(Placement placement, bool sameCard = false, bool pause = false)
    {
        var table = new InterpreterTable(new MtgEngine.Rules.Layers.LayerSystem());
        table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new StaticAbility(new PlayWithTopCardRevealed(new You()))));
        if (pause)
            for (var index = 0; index < 2; index++)
                table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
                    new ReplacementAbility(new WouldShuffle(new You()), new ModifyAmount(Add: 0))));
        var first = table.Card(table.A, Zone.Library);
        var second = table.Card(table.A, Zone.Library);
        var spell = table.OnStack(table.A, new Sequence([
            new Search(ObjectFilter.Any, Zone.Library, Who: new EachPlayer(), LibraryOf: new You(),
                Where: placement, RememberAs: "found"),
            new May(new GainLife(new Fixed(1))),
            new Exile(new Remembered("found")),
        ]));
        var engine = Subsystems.Wave2();
        var initial = table.State with
        {
            RevealedLibraryTops = ImmutableDictionary<PlayerId, ObjectId>.Empty.Add(table.A, first),
        };
        var result = engine.Interpreter.Resolve(initial, spell);
        var events = result.Events;
        var state = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        foreach (var chosen in new[] { first, sameCard ? first : second })
        {
            var pending = Assert.IsType<PendingChoice>(state.Pending);
            Assert.Equal("search", pending.Kind);
            result = engine.Choices.Answer(state, new AnswerChoice(pending.Player, pending.Id,
                [pending.Options.Single(option => option.Object == chosen).Id]));
            events = events.AddRange(result.Events);
            state = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        }
        var questions = 0;
        while (state.Pending is { Kind: "replacement-order" } replacement && questions < 4)
        {
            questions++;
            foreach (var viewer in new[] { table.A, table.B })
            {
                var view = engine.Views.Project(state, viewer);
                Assert.All(view.InspectedLibraryCards, card => Assert.Contains(card.Id,
                    state.In(ZoneKey.Of(Zone.Library, table.A))));
            }
            result = engine.Choices.Answer(state, new AnswerChoice(replacement.Player, replacement.Id,
                [replacement.Options[0].Id]));
            events = events.AddRange(result.Events);
            state = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        }
        Assert.Equal(pause ? 2 : 0, questions);
        var confirm = Assert.IsType<PendingChoice>(state.Pending);
        Assert.Equal("may", confirm.Kind);
        Assert.DoesNotContain(first, state.Objects.Keys);
        var remembered = confirm.Context.Extra["remembered:found"].Split(',')
            .Select(value => new ObjectId(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))).ToList();
        Assert.Equal(sameCard ? 1 : 2, remembered.Count);
        Assert.All(remembered, id => Assert.Contains(id, state.In(ZoneKey.Of(Zone.Library, table.A))));
        result = engine.Choices.Answer(state, new AnswerChoice(confirm.Player, confirm.Id, ["no"]));
        state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(result.Events)));
        Assert.Null(state.Pending);
        Assert.Equal(sameCard ? 1 : 0, state.In(ZoneKey.Of(Zone.Library, table.A)).Count);
        Assert.Equal(sameCard ? 1 : 2, state.In(ZoneKey.Shared(Zone.Exile)).Count);
        Assert.Empty(state.LibraryInspections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Shared_library_selections_do_not_duplicate_cards_and_survive_replay(bool sameCard, bool pause = false, bool shuffle = false)
    {
        var table = new InterpreterTable();
        var first = table.Card(table.A, Zone.Library);
        var second = table.Card(table.A, Zone.Library);
        if (shuffle)
            for (var index = 0; index < 3; index++)
                table.Put(table.A, ZoneKey.Of(Zone.Library, table.A), InterpreterTable.Definition(CardType.Land));
        var initialCount = shuffle ? 5 : 2;
        if (pause)
            for (var index = 0; index < 2; index++)
                table.Put(table.A, ZoneKey.Shared(Zone.Battlefield),
                    InterpreterTable.Definition(CardType.Enchantment,
                        new ReplacementAbility(new WouldBePutIntoZone(ObjectFilter.Creature with { In = Zone.Library }, Zone.Hand),
                            new ModifyAmount(Add: 0))));
        var spell = table.OnStack(table.A, new Search(ObjectFilter.Creature, Zone.Hand,
            Who: new EachPlayer(), LibraryOf: new You(), ShuffleAfter: shuffle));
        var engine = Subsystems.Wave2();
        var initial = table.State;
        var result = engine.Interpreter.Resolve(initial, spell);
        var events = result.Events;
        var state = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        foreach (var searcher in new[] { table.A, table.B })
        {
            var pending = Assert.IsType<PendingChoice>(state.Pending);
            Assert.Equal(searcher, pending.Player);
            Assert.Equal(2, pending.Options.Count);
            Assert.Empty(events.OfType<ObjectMoved>());
            Assert.Empty(events.OfType<ZoneReordered>());
            foreach (var viewer in new[] { table.A, table.B })
            {
                var view = engine.Views.Project(state, viewer);
                Assert.Equal(initialCount, view.InspectedLibraryCards.Count);
                Assert.All(view.InspectedLibraryCards, card => Assert.Equal(table.A, card.Owner));
                Assert.Equal(viewer == searcher ? 2 : 0, view.ChoiceCards.Count);
            }
            var chosen = searcher == table.A || sameCard ? first : second;
            result = engine.Choices.Answer(state, new AnswerChoice(searcher, pending.Id,
                [pending.Options.Single(option => option.Object == chosen).Id]));
            events = events.AddRange(result.Events);
            state = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        }
        var replacements = 0;
        while (state.Pending is { } pending && replacements < 4)
        {
            Assert.True(pause);
            Assert.Equal("replacement-order", pending.Kind);
            Assert.Empty(events.OfType<ZoneReordered>());
            foreach (var viewer in new[] { table.A, table.B })
            {
                var view = engine.Views.Project(state, viewer);
                Assert.Null(view.Pending!.Context.ZoneMoveSnapshot);
                Assert.Empty(view.Pending.Context.DeferredZoneMoves);
                Assert.All(view.InspectedLibraryCards, card =>
                    Assert.Contains(card.Id, state.In(ZoneKey.Of(Zone.Library, table.A))));
            }
            result = engine.Choices.Answer(state, new AnswerChoice(pending.Player, pending.Id, [pending.Options[0].Id]));
            events = events.AddRange(result.Events);
            state = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
            replacements++;
        }
        Assert.Equal(pause ? sameCard ? 1 : 2 : 0, replacements);
        Assert.Null(state.Pending);
        Assert.Empty(state.LibraryInspections);
        var moved = events.OfType<ObjectMoved>().Where(move => move.To.Zone.Zone == Zone.Hand).ToList();
        Assert.Equal(sameCard ? 1 : 2, moved.Count);
        Assert.Equal(moved.Count, moved.Select(move => move.From).Distinct().Count());
        Assert.Equal(initialCount - moved.Count, state.In(ZoneKey.Of(Zone.Library, table.A)).Count);
        Assert.Equal(moved.Count, state.In(ZoneKey.Of(Zone.Hand, table.A)).Count);
        Assert.Empty(state.In(ZoneKey.Of(Zone.Hand, table.B)));
        Assert.Equal(initialCount, state.Objects.Values.Count(card => card.Zone.Zone is Zone.Library or Zone.Hand));
        if (shuffle)
        {
            var firstShuffle = events.FindIndex(e => e is ZoneReordered { Shuffle: true });
            Assert.True(firstShuffle > events.FindLastIndex(e => e is ObjectMoved move && move.To.Zone.Zone == Zone.Hand));
            Assert.NotEqual(initial.Rng, state.Rng);
            var replay = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
            Assert.Equal(result.State.Rng, replay.Rng);
            Assert.Equal(result.State.In(ZoneKey.Of(Zone.Library, table.A)), replay.In(ZoneKey.Of(Zone.Library, table.A)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replacement_side_effects_accumulate_on_live_state_across_search_batch_members(bool pause)
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Library);
        foreach (var amount in pause ? new[] { 2, 3 } : new[] { 2 })
        {
            table.Put(table.A, ZoneKey.Shared(Zone.Battlefield),
                InterpreterTable.Definition(CardType.Enchantment,
                    new ReplacementAbility(new WouldBePutIntoZone(ObjectFilter.Any with { In = Zone.Library }, Zone.Hand),
                        new ReplaceWith(new GainLife(new Fixed(amount))))
                    { Text = $"Gain {amount} instead" }));
        }
        var spell = table.OnStack(table.A, new Search(ObjectFilter.Any, Zone.Hand, UpTo: new Fixed(2), ShuffleAfter: false));
        var engine = Subsystems.Wave2();
        var started = engine.Interpreter.Resolve(table.State, spell);
        var pending = started.State.Pending!;
        var answer = engine.Choices.Answer(started.State, new AnswerChoice(table.A, pending.Id,
            [.. pending.Options.Select(option => option.Id)]));
        var state = GameReducer.Apply(started.State, EventLogSerializer.Read(EventLogSerializer.Write(answer.Events)));
        var events = answer.Events;
        var questions = 0;
        while (state.Pending is { } choice && questions < 4)
        {
            questions++;
            Assert.Equal("replacement-order", choice.Kind);
            answer = engine.Choices.Answer(state, new AnswerChoice(choice.Player, choice.Id,
                [choice.Options.Single(option => option.Label == "Gain 2 instead").Id]));
            state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(answer.Events)));
            events = events.AddRange(answer.Events);
        }
        Assert.Equal(pause ? 2 : 0, questions);
        Assert.Null(state.Pending);
        Assert.Equal(table.State.Player(table.A).Life + 4, state.Player(table.A).Life);
        Assert.Equal(2, events.OfType<LifeChanged>().Count(change => change.Player == table.A && change.Delta == 2));
        Assert.Equal(2, state.In(ZoneKey.Of(Zone.Library, table.A)).Count);
        Assert.Empty(state.In(ZoneKey.Of(Zone.Hand, table.A)));
        Assert.Empty(state.LibraryInspections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_replacement_source_entering_with_search_results_does_not_affect_the_other_arrival(bool throughSearch, bool pause = false)
    {
        var table = new InterpreterTable();
        var source = table.Put(table.A, ZoneKey.Of(Zone.Library, table.A),
            InterpreterTable.Definition(CardType.Creature,
                new ReplacementAbility(new WouldEnterBattlefield(ObjectFilter.Creature), new EntersTapped())));
        var other = table.Put(table.A, ZoneKey.Of(Zone.Library, table.A),
            InterpreterTable.Definition(CardType.Creature));
        if (pause)
        {
            foreach (var amount in new[] { 1, 2 })
            {
                table.Put(table.A, ZoneKey.Shared(Zone.Battlefield),
                    InterpreterTable.Definition(CardType.Enchantment,
                        new ReplacementAbility(new WouldEnterBattlefield(ObjectFilter.Creature),
                            new EntersWithCounters(Counters.PlusOnePlusOne, new Fixed(amount)))
                        { Text = $"Enter with {amount} counters" }));
            }
        }
        var engine = Subsystems.Wave2();
        Outcome result;
        if (throughSearch)
        {
            var spell = table.OnStack(table.A, new Search(ObjectFilter.Creature, Zone.Battlefield,
                UpTo: new Fixed(2), ShuffleAfter: false));
            var started = engine.Interpreter.Resolve(table.State, spell);
            var pending = started.State.Pending!;
            result = engine.Choices.Answer(started.State, new AnswerChoice(table.A, pending.Id,
                [pending.Options.Single(option => option.Object == source).Id,
                    pending.Options.Single(option => option.Object == other).Id]));
        }
        else
        {
            result = engine.Zones.MoveAll(table.State,
                [new MoveRequest(source, ZoneKey.Shared(Zone.Battlefield)),
                    new MoveRequest(other, ZoneKey.Shared(Zone.Battlefield))]);
        }
        var events = result.Events;
        var state = result.State;
        for (var attempts = 0; state.Pending is { } pending && attempts < 8; attempts++)
        {
            Assert.Equal("replacement-order", pending.Kind);
            Assert.Null(engine.Views.Project(state, table.A).Pending!.Context.ZoneMoveSnapshot);
            Assert.Null(engine.Views.Project(state, table.B).Pending!.Context.ZoneMoveSnapshot);
            var selected = pending.Options.First(option => option.Label.StartsWith("Enter with", StringComparison.Ordinal));
            var resumed = engine.Choices.Answer(state, new AnswerChoice(pending.Player, pending.Id, [selected.Id]));
            state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(resumed.Events)));
            events = events.AddRange(resumed.Events);
        }
        Assert.Null(state.Pending);
        var arrival = Assert.Single(events.OfType<ObjectMoved>(), moved => moved.From == other);
        Assert.False(arrival.To.Tapped);
        Assert.False(state.Objects[arrival.To.Id].Tapped);
    }
}
