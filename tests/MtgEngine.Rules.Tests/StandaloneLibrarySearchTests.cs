using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "Deck")]
public sealed class StandaloneLibrarySearchTests
{
    [Theory]
    [InlineData(Zone.Hand)]
    [InlineData(Zone.Graveyard)]
    [InlineData(Zone.Exile)]
    [InlineData(Zone.Battlefield)]
    [InlineData(Zone.Library)]
    public void Manual_search_is_owned_retry_safe_and_uses_normal_choice_commands(Zone destination)
    {
        var table = new InterpreterTable();
        var own = table.Card(table.A, Zone.Library);
        var other = table.Card(table.B, Zone.Library);
        var initial = GameReducer.Apply(table.State,
            [new TurnBegan(1, table.A, false), new StepBegan(Step.PrecombatMain), new PriorityGranted(table.A)]);
        var command = new ManualLibrarySearch(table.A, Guid.NewGuid(), 1, destination,
            Filter: ObjectFilter.Creature, Tapped: true, Where: Placement.Top);
        var game = new Game(Subsystems.Wave2(), new EmptyDefinitions());
        var started = Assert.IsType<Applied>(game.Apply(initial, command));
        var state = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(started.Events)));
        var pending = Assert.IsType<PendingChoice>(state.Pending);
        Assert.Equal(own, Assert.Single(pending.Options).Object);
        Assert.Empty(game.Engine.Views.Project(state, table.B).InspectedLibraryCards);
        Assert.IsType<Refused>(game.Apply(state, command));
        game = new Game(Subsystems.Wave2(), new EmptyDefinitions());
        var finished = Assert.IsType<Applied>(game.Apply(state,
            new AnswerChoice(table.A, pending.Id, [pending.Options[0].Id])));
        state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(finished.Events)));
        Assert.Null(state.Pending);
        Assert.Empty(state.LibraryInspections);
        var key = destination is Zone.Battlefield or Zone.Exile ? ZoneKey.Shared(destination) : ZoneKey.Of(destination, table.A);
        var arrived = Assert.Single(state.In(key));
        if (destination == Zone.Battlefield)
            Assert.True(state.Objects[arrived].Tapped);
        Assert.Equal(other, Assert.Single(state.In(ZoneKey.Of(Zone.Library, table.B))));
        Assert.Single(started.Events.OfType<ManualActionTaken>());
        Assert.Single(started.Events.OfType<LibrarySearched>());
        Assert.Empty(finished.Events.OfType<LibrarySearched>());
        Assert.Single(finished.Events.OfType<ZoneReordered>(), change => change.Shuffle);
        Assert.IsType<Refused>(game.Apply(state, command));
    }

    private sealed class EmptyDefinitions : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => null;
        public CardDefinition? ByOracleId(string oracleId) => null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Search_survives_serialized_movement_and_shuffle_replacement_prompts(bool replaceMoves)
    {
        var table = new InterpreterTable();
        for (var index = 0; index < 2; index++)
        {
            table.Card(table.A, Zone.Library);
            table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
                new ReplacementAbility(new WouldShuffle(new You()), new ModifyAmount(Add: 0))));
            if (replaceMoves)
                table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
                    new ReplacementAbility(new WouldBePutIntoZone(ObjectFilter.Creature with { In = Zone.Library }, Zone.Hand),
                        new ReplaceWith(new GainLife(new Fixed(1))))));
        }
        var initial = table.State;
        var started = Subsystems.Wave2().Interpreter.Run(initial,
            new Search(ObjectFilter.Any, Zone.Hand, UpTo: new Fixed(2)), table.Context());
        var events = started.Events;
        var state = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        var selection = Assert.IsType<PendingChoice>(state.Pending);
        var selected = Subsystems.Wave2().Choices.Answer(state,
            new AnswerChoice(selection.Player, selection.Id, [.. selection.Options.Select(option => option.Id)]));
        events = events.AddRange(selected.Events);
        var pauses = 0;
        while (pauses < 4)
        {
            state = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
            if (state.Pending is not { } pending)
                break;
            Assert.Equal("replacement-order", pending.Kind);
            Assert.Null(pending.Context.Source);
            var engine = Subsystems.Wave2();
            foreach (var viewer in new[] { table.A, table.B })
                Assert.DoesNotContain(engine.Views.Project(state, viewer).Pending!.Context.Extra.Keys,
                    key => key.StartsWith(":", StringComparison.Ordinal));
            Assert.Empty(engine.Views.Project(state, table.B).InspectedLibraryCards);
            var resumed = engine.Choices.Answer(state,
                new AnswerChoice(pending.Player, pending.Id, [pending.Options[0].Id]));
            events = events.AddRange(resumed.Events);
            pauses++;
        }
        state = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        Assert.Equal(replaceMoves ? 3 : 1, pauses);
        Assert.Null(state.Pending);
        Assert.Empty(state.LibraryInspections);
        Assert.Equal(replaceMoves ? 2 : 0, state.In(ZoneKey.Of(Zone.Library, table.A)).Count);
        Assert.Equal(replaceMoves ? 0 : 2, state.In(ZoneKey.Of(Zone.Hand, table.A)).Count);
        Assert.Equal(replaceMoves ? 22 : 20, state.Player(table.A).Life);
        Assert.Single(events.OfType<LibrarySearched>());
        Assert.Single(events.OfType<ZoneReordered>(), change => change.Shuffle);
        Assert.Empty(events.OfType<StackObjectResolved>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Source_less_search_resumes_from_serialized_prompt_without_a_catalog_card(bool find)
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Library);
        var initial = table.State;
        var started = table.Run(new Search(ObjectFilter.Any, Zone.Hand, UpTo: new Fixed(1)));
        var restored = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(started)));
        var pending = Assert.IsType<PendingChoice>(restored.Pending);
        Assert.Null(pending.Context.Source);
        var shown = ChoiceSecrets.Withheld(pending)!;
        Assert.DoesNotContain(shown.Context.Extra.Keys, key => key.StartsWith(":", StringComparison.Ordinal));
        Assert.Equal(pending.Options, shown.Options);
        var selected = find ? ImmutableList.Create(pending.Options[0]) : [];
        var answered = new ChoiceAnswered(pending.Id, pending.Player, [.. selected.Select(option => option.Id)]);
        var resumed = new InterpreterTable().Subject.Resume(answered.Apply(restored),
            new Resumption(pending.Kind, pending.Player, selected, pending.Context));
        var replayed = GameReducer.Apply(restored,
            EventLogSerializer.Read(EventLogSerializer.Write(ImmutableList.Create<GameEvent>(answered).AddRange(resumed.Events))));
        Assert.Null(replayed.Pending);
        Assert.Empty(replayed.LibraryInspections);
        Assert.Equal(find ? 1 : 0, replayed.In(ZoneKey.Of(Zone.Hand, table.A)).Count);
        Assert.Equal(find ? 1 : 2, replayed.In(ZoneKey.Of(Zone.Library, table.A)).Count);
        Assert.Single(started.OfType<LibrarySearched>());
        Assert.Empty(resumed.Events.OfType<LibrarySearched>());
        Assert.Single(resumed.Events.OfType<ZoneReordered>());
        Assert.Empty(replayed.In(ZoneKey.Shared(Zone.Stack)));
    }
}
